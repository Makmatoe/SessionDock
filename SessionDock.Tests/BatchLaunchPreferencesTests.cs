using System.Reflection;
using System.Text.Json;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class BatchLaunchPreferencesTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-batch-preferences-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  Family\r\n  and\tFriends  ", "Family and Friends")]
    public void NormalizeAccountGroup_CollapsesWhitespaceAndAllowsEmpty(
        string? input,
        string? expected)
    {
        Assert.Equal(
            expected,
            BatchLaunchPreferences.NormalizeAccountGroup(input));
    }

    [Fact]
    public void NormalizeAccountGroup_BoundsPersistedText()
    {
        var normalized = BatchLaunchPreferences.NormalizeAccountGroup(
            new string('g', 200));

        Assert.NotNull(normalized);
        Assert.Equal(
            BatchLaunchPreferences.MaximumAccountGroupLength,
            normalized.Length);
    }

    [Fact]
    public void NormalizeDisplayText_DoesNotSplitUnicodeScalarAtBoundary()
    {
        var input = $"A{string.Concat(Enumerable.Repeat("😀", 20))}";

        var group = BatchLaunchPreferences.NormalizeAccountGroup(input);
        var presetName = BatchLaunchPreferences.NormalizePresetName(input);

        Assert.NotNull(group);
        Assert.NotNull(presetName);
        Assert.True(group.Length <= BatchLaunchPreferences.MaximumAccountGroupLength);
        Assert.True(presetName.Length <= BatchLaunchPreferences.MaximumPresetNameLength);
        Assert.False(char.IsHighSurrogate(group[^1]));
        Assert.False(char.IsHighSurrogate(presetName[^1]));
        Assert.DoesNotContain('\uFFFD', group);
        Assert.DoesNotContain('\uFFFD', presetName);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(30, 30)]
    [InlineData(0, BatchLaunchPreferences.DefaultDelaySeconds)]
    [InlineData(999, BatchLaunchPreferences.DefaultDelaySeconds)]
    public void NormalizeDelaySeconds_AllowsOnlyOfferedValues(
        int input,
        int expected)
    {
        Assert.Equal(
            expected,
            BatchLaunchPreferences.NormalizeDelaySeconds(input));
    }

    [Fact]
    public void NormalizePresets_RemovesStaleDuplicateAndCorruptEntries()
    {
        var first = CreateAccount('a', 1);
        var second = CreateAccount('b', 2);
        var staleKey = new string('f', 32);
        var presets = new BatchLaunchPreset?[]
        {
            new()
            {
                Name = "  Evening\r\n squad  ",
                AccountKeys =
                [first.Key.ToUpperInvariant(), staleKey, second.Key, first.Key],
                DelaySeconds = 999
            },
            new()
            {
                Name = "evening squad",
                AccountKeys = [first.Key, second.Key],
                DelaySeconds = 3
            },
            new()
            {
                Name = "Stale",
                AccountKeys = [first.Key, staleKey],
                DelaySeconds = 5
            },
            new()
            {
                Name = "No keys",
                AccountKeys = null!,
                DelaySeconds = 5
            },
            null
        };

        var normalized = BatchLaunchPreferences.NormalizePresets(
            presets!,
            [first, second]);

        var preset = Assert.Single(normalized);
        Assert.Equal("Evening squad", preset.Name);
        Assert.Equal([first.Key, second.Key], preset.AccountKeys);
        Assert.Equal(
            BatchLaunchPreferences.DefaultDelaySeconds,
            preset.DelaySeconds);
    }

    [Fact]
    public void PrunePresetsForCurrentAccounts_UpdatesAndThenDropsRemovedSelection()
    {
        var first = CreateAccount('a', 1);
        var second = CreateAccount('b', 2);
        var third = CreateAccount('c', 3);
        var settings = new AppSettings
        {
            Accounts = [first, second, third],
            BatchLaunchPresets =
            [
                new BatchLaunchPreset
                {
                    Name = "Trio",
                    AccountKeys = [first.Key, second.Key, third.Key],
                    DelaySeconds = 10
                }
            ]
        };

        settings.Accounts.Remove(third);
        BatchLaunchPreferences.PrunePresetsForCurrentAccounts(settings);

        Assert.Equal(
            [first.Key, second.Key],
            Assert.Single(settings.BatchLaunchPresets).AccountKeys);

        settings.Accounts.Remove(second);
        BatchLaunchPreferences.PrunePresetsForCurrentAccounts(settings);

        Assert.Empty(settings.BatchLaunchPresets);
    }

    [Fact]
    public void ResolveAccounts_PreservesRequestedOrderAndCanonicalKeys()
    {
        var first = CreateAccount('a', 1);
        var second = CreateAccount('b', 2);

        var resolved = BatchLaunchPreferences.ResolveAccounts(
            [second.Key.ToUpperInvariant(), "stale", first.Key, second.Key],
            [first, second]);

        Assert.Equal([second, first], resolved);
        Assert.Same(second, resolved[0]);
        Assert.Same(first, resolved[1]);
    }

    [Fact]
    public void GetRetryAccountKeys_UsesStructuredKeysAndDropsStaleAccounts()
    {
        var first = CreateAccount('a', 1);
        var second = CreateAccount('b', 2);

        var keys = BatchLaunchPreferences.GetRetryAccountKeys(
            [second.Key, null, "@display-name: failure", first.Key, second.Key],
            [first, second]);

        Assert.Equal([second.Key, first.Key], keys);
    }

    [Fact]
    public void TryCreatePreset_StoresOnlyStableKeysAndDelay()
    {
        var first = CreateAccount('a', 1);
        first.Destination =
            "https://www.roblox.com/share?code=private-code&type=Server";
        var second = CreateAccount('b', 2);
        second.Destination = "private-server-code";

        var created = BatchLaunchPreferences.TryCreatePreset(
            "  My\tteam  ",
            [first, second],
            15,
            out var preset,
            out var error);

        Assert.True(created, error);
        Assert.NotNull(preset);
        Assert.Equal("My team", preset.Name);
        Assert.Equal([first.Key, second.Key], preset.AccountKeys);
        Assert.Equal(15, preset.DelaySeconds);
        var properties = typeof(BatchLaunchPreset)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["AccountKeys", "DelaySeconds", "Name"], properties);
        var json = JsonSerializer.Serialize(preset);
        Assert.DoesNotContain("private-code", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-server-code", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Destination", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NormalizesCorruptBatchPreferencesAndPersistsRepair()
    {
        Directory.CreateDirectory(_storageDirectory);
        var first = CreateAccount('a', 1);
        first.Group = "  Family\r\n friends ";
        var second = CreateAccount('b', 2);
        var settings = new AppSettings
        {
            Accounts = [first, second],
            ActiveAccountKey = first.Key,
            BatchLaunchDelaySeconds = -100,
            BatchLaunchPresets =
            [
                new BatchLaunchPreset
                {
                    Name = "  Main  ",
                    AccountKeys = [first.Key, "stale", second.Key],
                    DelaySeconds = 123
                },
                new BatchLaunchPreset
                {
                    Name = "Broken",
                    AccountKeys = ["stale"],
                    DelaySeconds = 5
                }
            ]
        };
        File.WriteAllText(
            Path.Combine(_storageDirectory, "settings.json"),
            JsonSerializer.Serialize(settings));

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Equal("Family friends", loaded.Accounts[0].Group);
        Assert.Equal(
            BatchLaunchPreferences.DefaultDelaySeconds,
            loaded.BatchLaunchDelaySeconds);
        var preset = Assert.Single(loaded.BatchLaunchPresets);
        Assert.Equal("Main", preset.Name);
        Assert.Equal([first.Key, second.Key], preset.AccountKeys);
        Assert.Equal(
            BatchLaunchPreferences.DefaultDelaySeconds,
            preset.DelaySeconds);

        var reloaded = new SettingsService(_storageDirectory).Load();
        Assert.Equal("Family friends", reloaded.Accounts[0].Group);
        Assert.Equal(
            BatchLaunchPreferences.DefaultDelaySeconds,
            reloaded.BatchLaunchDelaySeconds);
        Assert.Single(reloaded.BatchLaunchPresets);
    }

    [Fact]
    public void RetryUi_UsesFailedOnlyReviewAndWarnsAboutClosingClients()
    {
        var root = FindRepositoryRoot();
        var batchSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Batch.cs"));
        var dialogSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "BatchLaunchDialog.xaml.cs"));

        Assert.Contains(
            "result.Failures.Select(failure => failure.AccountKey)",
            batchSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Parse",
            batchSource[
                batchSource.IndexOf("private void SetBatchRetryState", StringComparison.Ordinal)..
                batchSource.IndexOf(
                    "private async Task<WebSessionToken?> ActivateBatchAccountAsync",
                    StringComparison.Ordinal)],
            StringComparison.Ordinal);
        Assert.Contains(
            "including clients started by the previous batch",
            dialogSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "var minimumSelection = _retryMode ? 1 : 2;",
            dialogSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BatchPreferenceUi_ExposesAccessibleGroupPresetAndRetryControls()
    {
        var root = FindRepositoryRoot();
        var appearanceXaml = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "AccountAppearanceDialog.xaml"));
        var batchXaml = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "BatchLaunchDialog.xaml"));
        var mainWindowXaml = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml"));

        Assert.Contains("x:Name=\"GroupBox\"", appearanceXaml, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"Account group\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PresetComboBox\"", batchXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"LoadPresetButton_Click\"", batchXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SavePresetButton_Click\"", batchXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"DeletePresetButton_Click\"", batchXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SelectGroupButton_Click\"", batchXaml, StringComparison.Ordinal);
        Assert.Contains(
            "Starting the batch closes every currently running verified Roblox Player instance.",
            batchXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"RetryFailedBatchButton\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Click=\"RetryFailedBatchButton_Click\"",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.HelpText=\"Review only the accounts that failed in the last batch\"",
            mainWindowXaml,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }

    private static AccountProfile CreateAccount(char keyCharacter, long userId)
    {
        var key = new string(keyCharacter, 32);
        return new AccountProfile
        {
            Key = key,
            UserId = userId,
            Username = $"user{userId}",
            SessionFolder = $@"Profiles\{key}",
            Destination = (1000 + userId).ToString()
        };
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "SessionDock.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "The SessionDock repository root could not be located.");
    }
}
