using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

[Collection(typeof(TimingSensitiveTestCollection))]
public sealed class LocalizationTests : IDisposable
{
    private static readonly string[] UserFacingAttributeNames =
    [
        "Title",
        "Text",
        "Content",
        "ToolTip",
        "AutomationProperties.Name",
        "AutomationProperties.HelpText",
        "AutomationProperties.ItemStatus"
    ];

    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-localization-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    [InlineData(" SYSTEM ", "system")]
    [InlineData("EN-us", "en-US")]
    [InlineData(" nl-nl ", "nl-NL")]
    [InlineData("fr-FR", "system")]
    [InlineData("nl", "system")]
    [InlineData("../../nl-NL.xaml", "system")]
    public void Normalize_AllowsOnlySupportedCanonicalPreferences(
        string? value,
        string expected)
    {
        Assert.Equal(expected, LocalizationPreference.Normalize(value));
    }

    [Theory]
    [InlineData("system", "nl-BE", "nl-NL")]
    [InlineData("system", "de-DE", "en-US")]
    [InlineData("en-US", "nl-NL", "en-US")]
    [InlineData("nl-NL", "en-US", "nl-NL")]
    public void Resolve_UsesSupportedSystemLanguageOrEnglishFallback(
        string preference,
        string systemCulture,
        string expected)
    {
        Assert.Equal(
            expected,
            LocalizationPreference.Resolve(
                preference,
                CultureInfo.GetCultureInfo(systemCulture)));
    }

    [Fact]
    public void NewAndLegacySettings_DefaultToSystemLanguage()
    {
        Assert.Equal(
            LocalizationPreference.System,
            new AppSettings().Language);

        var service = new SettingsService(_storageDirectory);
        service.Save(CreateSettings(LocalizationPreference.Dutch));
        var path = Path.Combine(_storageDirectory, "settings.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(document.Remove(nameof(AppSettings.Language)));
        File.WriteAllText(path, document.ToJsonString());

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Equal(LocalizationPreference.System, loaded.Language);
    }

    [Theory]
    [InlineData("nl-NL", "nl-NL")]
    [InlineData("EN-us", "en-US")]
    [InlineData("unsupported", "system")]
    public void SettingsLoad_StrictlyNormalizesAndPersistsLanguage(
        string stored,
        string expected)
    {
        var service = new SettingsService(_storageDirectory);
        service.Save(CreateSettings(stored));

        var loaded = new SettingsService(_storageDirectory).Load();
        var reloaded = new SettingsService(_storageDirectory).Load();

        Assert.Equal(expected, loaded.Language);
        Assert.Equal(expected, reloaded.Language);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task CommitLanguageChange_FailedWriteRollsBackWithoutApplying(
        Type failureType)
    {
        var settings = CreateSettings(LocalizationPreference.English);
        Exception failure = failureType == typeof(IOException)
            ? new IOException("disk unavailable")
            : new UnauthorizedAccessException("write denied");
        var coordinator = new SettingsMutationCoordinator(
            settings,
            new SerializedSettingsWriter(_ => throw failure));
        var visualLanguageApplied = false;

        var result = await coordinator.CommitAsync(
            () => settings.Language = LocalizationPreference.Dutch,
            () => visualLanguageApplied = true);

        Assert.False(result.Committed);
        Assert.IsType(failureType, result.Failure);
        Assert.Equal(LocalizationPreference.English, settings.Language);
        Assert.False(visualLanguageApplied);
    }

    [Fact]
    public void LocalizationDictionaries_HaveMatchingNonEmptyKeys()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Localization");
        var english = ReadStrings(Path.Combine(
            directory,
            "Strings.en-US.xaml"));
        var dutch = ReadStrings(Path.Combine(
            directory,
            "Strings.nl-NL.xaml"));

        Assert.True(english.Count >= 250);
        Assert.Equal(
            english.Keys.Order(StringComparer.Ordinal),
            dutch.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(english.Values, string.IsNullOrWhiteSpace);
        Assert.DoesNotContain(dutch.Values, string.IsNullOrWhiteSpace);
        Assert.NotEqual(english["Language.Title"], dutch["Language.Title"]);
    }

    [Fact]
    public void ProductionXaml_UserFacingStaticAttributesUseLocalizationResources()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     applicationDirectory,
                     "*.xaml",
                     SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(path).Equals(
                    "App.xaml",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var attribute in document.Descendants().Attributes())
            {
                if (!UserFacingAttributeNames.Contains(
                        attribute.Name.LocalName,
                        StringComparer.Ordinal) ||
                    string.IsNullOrWhiteSpace(attribute.Value) ||
                    attribute.Value.StartsWith('{') ||
                    attribute.Value is "SessionDock" or "SD")
                {
                    continue;
                }

                var line = ((System.Xml.IXmlLineInfo)attribute).LineNumber;
                violations.Add(
                    $"{Path.GetFileName(path)}:{line} " +
                    $"{attribute.Name.LocalName}=\"{attribute.Value}\"");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Static user-facing XAML text must use a live DynamicResource." +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void AppResources_KeepEnglishFallbackAlongsideThemePalette()
    {
        var appXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "App.xaml"));

        Assert.Contains(
            "Themes/DarkTheme.xaml",
            appXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Localization/Strings.en-US.xaml",
            appXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Localization/Strings.nl-NL.xaml",
            appXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionLocalizationReferences_ExistInEnglishFallback()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var english = ReadStrings(Path.Combine(
            applicationDirectory,
            "Localization",
            "Strings.en-US.xaml"));
        var references = new HashSet<string>(StringComparer.Ordinal);
        var xamlPattern = new Regex(
            @"\{DynamicResource\s+(?<key>[A-Za-z][A-Za-z0-9.]+)\}",
            RegexOptions.CultureInvariant);
        foreach (var path in Directory.EnumerateFiles(
                     applicationDirectory,
                     "*.xaml",
                     SearchOption.TopDirectoryOnly))
        {
            references.UnionWith(xamlPattern.Matches(File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value)
                .Where(key => key.Contains('.', StringComparison.Ordinal)));
        }

        var codePattern = new Regex(
            @"(?:Localize|GetString|Format)\s*\(\s*""(?<key>[A-Za-z][A-Za-z0-9.]+)""",
            RegexOptions.CultureInvariant);
        foreach (var path in Directory.EnumerateFiles(
                     applicationDirectory,
                     "*.cs",
                     SearchOption.TopDirectoryOnly))
        {
            references.UnionWith(codePattern.Matches(File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value)
                .Where(key => key.Contains('.', StringComparison.Ordinal)));
        }

        Assert.Empty(references.Except(english.Keys, StringComparer.Ordinal));
    }

    [Fact]
    public void LocalizationService_LiveSwitchesResourcesAndFallsBackSafely()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application();
                application.Resources.MergedDictionaries.Add(
                    new ResourceDictionary
                    {
                        Source = new Uri(
                            "/SessionDock;component/Localization/Strings.en-US.xaml",
                            UriKind.Relative)
                    });
                using var service = new AppLocalizationService(
                    application,
                    CultureInfo.GetCultureInfo("en-US"));
                var changes = 0;
                service.LanguageChanged += (_, _) => changes++;

                service.ApplyPreference(LocalizationPreference.Dutch);
                Assert.Equal("Taal", service.GetString("Language.Title"));
                Assert.Equal("nl-NL", service.EffectiveCulture.Name);

                service.ApplyPreference(LocalizationPreference.English);
                Assert.Equal("Language", service.GetString("Language.Title"));
                Assert.Equal("en-US", service.EffectiveCulture.Name);
                Assert.Equal("Missing.Key", service.GetString("Missing.Key"));
                Assert.Equal(2, changes);
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
            throw new InvalidOperationException("Live language test failed.", failure);
    }

    [Fact]
    public void DisplayDatesUseSelectedCulture_JsonSerializationDoesNot()
    {
        var value = new DateTimeOffset(
            2026,
            7,
            29,
            18,
            15,
            0,
            TimeSpan.Zero);
        var english = CultureInfo.GetCultureInfo("en-US");
        var dutch = CultureInfo.GetCultureInfo("nl-NL");

        var englishDisplay = LocalizationCulture.FormatLocalDateTime(
            value,
            english);
        var dutchDisplay = LocalizationCulture.FormatLocalDateTime(
            value,
            dutch);

        Assert.Equal(value.ToLocalTime().ToString("g", english), englishDisplay);
        Assert.Equal(value.ToLocalTime().ToString("g", dutch), dutchDisplay);
        Assert.NotEqual(englishDisplay, dutchDisplay);

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = english;
            var englishJson = JsonSerializer.Serialize(value);
            CultureInfo.CurrentCulture = dutch;
            var dutchJson = JsonSerializer.Serialize(value);
            Assert.Equal(englishJson, dutchJson);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }

    private static AppSettings CreateSettings(string language)
    {
        var accountKey = Guid.NewGuid().ToString("N");
        return new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = accountKey,
                    UserId = 42,
                    Username = "builder",
                    SessionFolder = $@"Profiles\{accountKey}"
                }
            ],
            ActiveAccountKey = accountKey,
            Language = language
        };
    }

    private static Dictionary<string, string> ReadStrings(string path)
    {
        Assert.True(File.Exists(path), $"Localization file missing: {path}");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Root!
            .Elements()
            .ToDictionary(
                element => (string)element.Attribute(xaml + "Key")!,
                element => element.Value,
                StringComparer.Ordinal);
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
