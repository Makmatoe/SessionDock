using System.Text;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class MetadataTransferServiceTests
{
    private const string AccountKey = "0123456789abcdef0123456789abcdef";
    private const string SensitiveUsername = "PrivateUsernameCanary";
    private const string SensitiveSessionFolder =
        "Profiles\\PrivateSessionFolderCanary";
    private const string SensitivePrivateCode = "PrivateCodeCanary_9843";
    private const string SensitiveJobId =
        "a18c877e-4070-4a84-a5f7-36668b46a77d";
    private const string SensitiveToken = "AuthenticationTicketCanary_9843";
    private const string SensitiveIntegration = "BearerIntegrationCanary_9843";

    [Fact]
    public void CreateExport_UsesAnExplicitSafeSchemaAndExcludesSecrets()
    {
        var settings = CreateSettingsWithCanaries();

        var package = MetadataTransferService.CreateExport(settings);

        Assert.Equal(1, package.AccountCount);
        Assert.Equal(1, package.PublicFavoriteCount);
        Assert.Contains("\"format\": \"sessiondock.metadata\"", package.Json);
        Assert.Contains("\"version\": 1", package.Json);
        Assert.Contains("\"robloxUserId\": 42", package.Json);
        Assert.Contains("\"label\": \"Primary\"", package.Json);
        Assert.Contains("\"group\": \"Friends\"", package.Json);
        Assert.Contains("\"color\": \"#7C5CFC\"", package.Json);
        Assert.Contains("\"placeId\": 12345", package.Json);
        Assert.Contains("\"customName\": \"Public favorite\"", package.Json);
        Assert.DoesNotContain(AccountKey, package.Json);
        Assert.DoesNotContain(SensitiveUsername, package.Json);
        Assert.DoesNotContain(SensitiveSessionFolder, package.Json);
        Assert.DoesNotContain(SensitivePrivateCode, package.Json);
        Assert.DoesNotContain(SensitiveJobId, package.Json);
        Assert.DoesNotContain(SensitiveToken, package.Json);
        Assert.DoesNotContain(SensitiveIntegration, package.Json);
        Assert.DoesNotContain("sessionFolder", package.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("destination", package.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jobId", package.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", package.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountKey", package.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("batch", package.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sound", package.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateExport_OmitsPrivateAndServerSpecificFavorites()
    {
        var settings = CreateSettingsWithCanaries();
        settings.RecentExperiences.Add(new RecentExperience
        {
            Destination = "54321",
            PlaceId = 54321,
            Name = "Server-specific canary",
            IsPinned = true,
            ServerJobId = SensitiveJobId,
            AccountUserId = 42,
            LastLaunchedAt = DateTimeOffset.UtcNow
        });
        settings.RecentExperiences.Add(new RecentExperience
        {
            Destination =
                $"https://www.roblox.com/games/67890/Test?privateServerLinkCode={SensitivePrivateCode}",
            PlaceId = 67890,
            Name = "Private favorite canary",
            IsPrivateServer = true,
            IsPinned = true,
            AccountUserId = 42,
            LastLaunchedAt = DateTimeOffset.UtcNow
        });

        var package = MetadataTransferService.CreateExport(settings);

        Assert.Equal(1, package.PublicFavoriteCount);
        Assert.DoesNotContain("54321", package.Json);
        Assert.DoesNotContain("67890", package.Json);
        Assert.DoesNotContain("Server-specific canary", package.Json);
        Assert.DoesNotContain("Private favorite canary", package.Json);
        Assert.DoesNotContain(SensitiveJobId, package.Json);
        Assert.DoesNotContain(SensitivePrivateCode, package.Json);
    }

    [Fact]
    public void ImportPlan_OnlyMergesMatchedMetadataAndSafePublicFavorites()
    {
        var firstKey = Guid.NewGuid().ToString("N");
        var secondKey = Guid.NewGuid().ToString("N");
        var settings = new AppSettings
        {
            Accounts =
            [
                CreateAccount(firstKey, 42, "First", "111"),
                CreateAccount(secondKey, 84, "Second", "222")
            ],
            ActiveAccountKey = firstKey,
            RecentExperiences =
            [
                new RecentExperience
                {
                    Destination =
                        $"https://www.roblox.com/games/999/Private?privateServerLinkCode={SensitivePrivateCode}",
                    PlaceId = 999,
                    IsPrivateServer = true,
                    IsPinned = true,
                    ServerJobId = SensitiveJobId,
                    AccountUserId = 42,
                    AccountUsername = "First",
                    LastLaunchedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        var json = $$"""
            {
              "format": "sessiondock.metadata",
              "version": 1,
              "accounts": [
                { "robloxUserId": 84, "label": "Alt", "group": "Team", "color": "#4D8DFF" },
                { "robloxUserId": 42, "label": "Main", "color": "#7C5CFC" },
                { "robloxUserId": 9999, "label": "Must not create" }
              ],
              "publicFavorites": [
                { "placeId": 12345, "accountUserId": 42, "name": "Public place", "customName": "Favorite" },
                { "placeId": 54321, "accountUserId": 9999, "name": "Unmatched" }
              ]
            }
            """;

        var plan = MetadataTransferService.CreateImportPlan(
            Encoding.UTF8.GetBytes(json),
            settings);

        Assert.True(plan.HasChanges);
        Assert.Equal(2, plan.AccountUpdateCount);
        Assert.True(plan.OrderWillChange);
        Assert.Equal(1, plan.FavoritesToAdd);
        Assert.Equal(0, plan.FavoritesToUpdate);
        Assert.Equal(1, plan.SkippedAccountCount);
        Assert.Equal(1, plan.SkippedFavoriteCount);
        Assert.Contains("Never imported:", plan.Preview);
        Assert.Contains("Skipped safely:", plan.Preview);

        plan.Apply(settings);

        Assert.Equal(2, settings.Accounts.Count);
        Assert.Equal([84L, 42L], settings.Accounts.Select(account => account.UserId));
        var first = settings.Accounts.Single(account => account.UserId == 42);
        Assert.Equal(firstKey, first.Key);
        Assert.Equal("First", first.Username);
        Assert.Equal($@"Profiles\{firstKey}", first.SessionFolder);
        Assert.Equal("111", first.Destination);
        Assert.Equal("Main", first.Label);
        Assert.Null(first.Group);
        Assert.Equal("#7C5CFC", first.ColorHex);
        var second = settings.Accounts.Single(account => account.UserId == 84);
        Assert.Equal(secondKey, second.Key);
        Assert.Equal("Second", second.Username);
        Assert.Equal("222", second.Destination);
        Assert.Equal("Alt", second.Label);
        Assert.Equal("Team", second.Group);
        Assert.Equal("#4D8DFF", second.ColorHex);
        Assert.DoesNotContain(settings.Accounts, account => account.UserId == 9999);

        var privateFavorite = settings.RecentExperiences.Single(item =>
            item.IsPrivateServer);
        Assert.Contains(SensitivePrivateCode, privateFavorite.Destination);
        Assert.Equal(SensitiveJobId, privateFavorite.ServerJobId);
        var publicFavorite = settings.RecentExperiences.Single(item =>
            item.PlaceId == 12345);
        Assert.Equal("12345", publicFavorite.Destination);
        Assert.Equal("Public place", publicFavorite.Name);
        Assert.Equal("Favorite", publicFavorite.CustomName);
        Assert.True(publicFavorite.IsPinned);
        Assert.False(publicFavorite.IsPrivateServer);
        Assert.Null(publicFavorite.ServerJobId);
        Assert.Equal(42, publicFavorite.AccountUserId);
        Assert.Equal("First", publicFavorite.AccountUsername);
    }

    [Fact]
    public void ImportPlan_PreservesUnmatchedAccountPositionsWhileReorderingMatches()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                CreateAccount(Guid.NewGuid().ToString("N"), 999, "Local only", "1"),
                CreateAccount(Guid.NewGuid().ToString("N"), 42, "First", "2"),
                CreateAccount(Guid.NewGuid().ToString("N"), 84, "Second", "3")
            ]
        };
        var json = """
            {
              "format": "sessiondock.metadata",
              "version": 1,
              "accounts": [
                { "robloxUserId": 84 },
                { "robloxUserId": 42 }
              ],
              "publicFavorites": []
            }
            """;

        var plan = MetadataTransferService.CreateImportPlan(
            Encoding.UTF8.GetBytes(json),
            settings);
        plan.Apply(settings);

        Assert.Equal(
            [999L, 84L, 42L],
            settings.Accounts.Select(account => account.UserId));
    }

    [Fact]
    public void ImportPlan_UpdatePreservesExistingServerMetadata()
    {
        var recent = new RecentExperience
        {
            Destination = "12345",
            PlaceId = 12345,
            Name = "Old",
            IsPinned = false,
            ServerJobId = SensitiveJobId,
            AccountUserId = 42,
            LastLaunchedAt = new DateTimeOffset(
                2026,
                1,
                1,
                0,
                0,
                0,
                TimeSpan.Zero)
        };
        var settings = new AppSettings
        {
            Accounts =
            [
                CreateAccount(
                    Guid.NewGuid().ToString("N"),
                    42,
                    "First",
                    "111")
            ],
            RecentExperiences = [recent]
        };
        var originalTimestamp = recent.LastLaunchedAt;
        var json = """
            {
              "format": "sessiondock.metadata",
              "version": 1,
              "accounts": [{ "robloxUserId": 42 }],
              "publicFavorites": [
                { "placeId": 12345, "accountUserId": 42, "name": "Updated" }
              ]
            }
            """;

        var plan = MetadataTransferService.CreateImportPlan(
            Encoding.UTF8.GetBytes(json),
            settings);
        plan.Apply(settings);

        Assert.Same(recent, Assert.Single(settings.RecentExperiences));
        Assert.Equal("Updated", recent.Name);
        Assert.True(recent.IsPinned);
        Assert.Equal(SensitiveJobId, recent.ServerJobId);
        Assert.Equal(originalTimestamp, recent.LastLaunchedAt);
    }

    [Fact]
    public void ImportPlan_SettingsSaveFailureRollsBackEveryChange()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                CreateAccount(
                    Guid.NewGuid().ToString("N"),
                    42,
                    "First",
                    "111")
            ]
        };
        settings.Accounts[0].Label = "Original";
        var originalAccount = settings.Accounts[0];
        var json = """
            {
              "format": "sessiondock.metadata",
              "version": 1,
              "accounts": [{ "robloxUserId": 42, "label": "Imported" }],
              "publicFavorites": [
                { "placeId": 12345, "accountUserId": 42, "name": "New" }
              ]
            }
            """;
        var plan = MetadataTransferService.CreateImportPlan(
            Encoding.UTF8.GetBytes(json),
            settings);

        var committed = SettingsMutation.TryCommit(
            settings,
            () => plan.Apply(settings),
            _ => throw new IOException("disk unavailable"),
            out var failure);

        Assert.False(committed);
        Assert.IsType<IOException>(failure);
        Assert.Same(originalAccount, Assert.Single(settings.Accounts));
        Assert.Equal("Original", originalAccount.Label);
        Assert.Empty(settings.RecentExperiences);
    }

    [Fact]
    public void ImportPreview_EnumeratesBeforeAfterOrderAndFavoriteChangesWithoutSecrets()
    {
        var firstKey = Guid.NewGuid().ToString("N");
        var secondKey = Guid.NewGuid().ToString("N");
        var first = CreateAccount(
            firstKey,
            42,
            SensitiveUsername,
            $"https://www.roblox.com/games/999/Private?privateServerLinkCode={SensitivePrivateCode}");
        first.Label = "Old main";
        first.Group = "Old group";
        first.ColorHex = "#7C5CFC";
        var second = CreateAccount(
            secondKey,
            84,
            "SecondUsernameCanary",
            "222");
        second.Label = "Old alt";
        second.ColorHex = "#4D8DFF";
        var settings = new AppSettings
        {
            Accounts = [first, second],
            RecentExperiences =
            [
                new RecentExperience
                {
                    Destination = "12345",
                    PlaceId = 12345,
                    Name = "Old place",
                    CustomName = "Old favorite",
                    IsPinned = false,
                    ServerJobId = SensitiveJobId,
                    AccountUserId = 42,
                    AccountUsername = SensitiveUsername,
                    LastLaunchedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        var json = """
            {
              "format": "sessiondock.metadata",
              "version": 1,
              "accounts": [
                { "robloxUserId": 84, "label": "New alt" },
                { "robloxUserId": 42, "label": "New main", "color": "#E36B8D" }
              ],
              "publicFavorites": [
                { "placeId": 12345, "accountUserId": 42, "name": "New place", "customName": "New favorite" },
                { "placeId": 54321, "accountUserId": 84, "name": "Added place" }
              ]
            }
            """;

        var plan = MetadataTransferService.CreateImportPlan(
            Encoding.UTF8.GetBytes(json),
            settings);

        Assert.Contains("Matched account appearance (current -> imported):", plan.Preview);
        Assert.Contains(
            "Roblox user 84: label \"Old alt\" -> \"New alt\"; group not set (unchanged); color \"#4D8DFF\" -> default (clear)",
            plan.Preview);
        Assert.Contains(
            "Roblox user 42: label \"Old main\" -> \"New main\"; group \"Old group\" -> not set (clear); color \"#7C5CFC\" -> \"#E36B8D\"",
            plan.Preview);
        Assert.Contains("Account order moves:", plan.Preview);
        Assert.Contains("Roblox user 84: position 2 -> 1", plan.Preview);
        Assert.Contains("Roblox user 42: position 1 -> 2", plan.Preview);
        Assert.Contains("Public favorite changes:", plan.Preview);
        Assert.Contains(
            "Update public place 12345 for Roblox user 42: display name \"Old place\" -> \"New place\"; favorite name \"Old favorite\" -> \"New favorite\"; pinned no -> yes",
            plan.Preview);
        Assert.Contains(
            "Add public place 54321 for Roblox user 84: display name \"Added place\"; favorite name not set; pinned yes",
            plan.Preview);
        Assert.DoesNotContain(firstKey, plan.Preview);
        Assert.DoesNotContain(secondKey, plan.Preview);
        Assert.DoesNotContain(SensitiveUsername, plan.Preview);
        Assert.DoesNotContain("SecondUsernameCanary", plan.Preview);
        Assert.DoesNotContain(SensitivePrivateCode, plan.Preview);
        Assert.DoesNotContain(SensitiveJobId, plan.Preview);
        Assert.DoesNotContain("Profiles\\", plan.Preview);
    }

    [Theory]
    [MemberData(nameof(RejectedDocuments))]
    public void CreateImportPlan_RejectsMalformedOrAmbiguousDocuments(string json)
    {
        var settings = new AppSettings();

        Assert.Throws<InvalidDataException>(() =>
            MetadataTransferService.CreateImportPlan(
                Encoding.UTF8.GetBytes(json),
                settings));
    }

    [Fact]
    public void CreateImportPlan_RejectsOversizedInput()
    {
        var oversized = new byte[MetadataTransferService.MaximumFileBytes + 1];

        Assert.Throws<InvalidDataException>(() =>
            MetadataTransferService.CreateImportPlan(
                oversized,
                new AppSettings()));
    }

    [Fact]
    public void CreateImportPlan_RejectsTypeTextAndCountBounds()
    {
        var overlongLabel = new string('L', 41);
        var tooManyAccounts = string.Join(
            ",",
            Enumerable.Range(1, MetadataTransferService.MaximumAccounts + 1)
                .Select(index => $"{{\"robloxUserId\":{index}}}"));
        var tooManyFavorites = string.Join(
            ",",
            Enumerable.Range(
                    1,
                    MetadataTransferService.MaximumPublicFavorites + 1)
                .Select(index =>
                    $"{{\"placeId\":{index},\"accountUserId\":0}}"));
        var documents = new[]
        {
            """
            {
              "format": "sessiondock.metadata",
              "version": "one",
              "accounts": [],
              "publicFavorites": []
            }
            """,
            $$"""
            {
              "format": "sessiondock.metadata",
              "version": 1,
              "accounts": [{ "robloxUserId": 42, "label": "{{overlongLabel}}" }],
              "publicFavorites": []
            }
            """,
            $$"""
            {
              "format": "sessiondock.metadata",
              "version": 1,
              "accounts": [{{tooManyAccounts}}],
              "publicFavorites": []
            }
            """,
            $$"""
            {
              "format": "sessiondock.metadata",
              "version": 1,
              "accounts": [],
              "publicFavorites": [{{tooManyFavorites}}]
            }
            """
        };

        foreach (var json in documents)
        {
            Assert.Throws<InvalidDataException>(() =>
                MetadataTransferService.CreateImportPlan(
                    Encoding.UTF8.GetBytes(json),
                    new AppSettings()));
        }
    }

    [Fact]
    public async Task FileOperations_UseASafeNameAndRejectDirectories()
    {
        Assert.Matches(
            "^[A-Za-z0-9.-]+$",
            MetadataExportPackage.SuggestedFileName);
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar,
            MetadataExportPackage.SuggestedFileName);
        Assert.DoesNotContain(
            Path.AltDirectorySeparatorChar,
            MetadataExportPackage.SuggestedFileName);

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock-MetadataPath-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var package = MetadataTransferService.CreateExport(
                new AppSettings());
            await Assert.ThrowsAsync<IOException>(() =>
                MetadataTransferService.ExportAsync(
                    directory,
                    package,
                    TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<IOException>(() =>
                MetadataTransferService.ReadImportAsync(
                    directory,
                    new AppSettings(),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_WritesTheExactReviewedJsonWithoutBom()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock-Metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var package = MetadataTransferService.CreateExport(
                CreateSettingsWithCanaries());
            var destination = Path.Combine(
                directory,
                MetadataExportPackage.SuggestedFileName);
            await File.WriteAllTextAsync(
                destination,
                "older export that must be atomically replaced",
                TestContext.Current.CancellationToken);

            await MetadataTransferService.ExportAsync(
                destination,
                package,
                TestContext.Current.CancellationToken);

            var bytes = await File.ReadAllBytesAsync(
                destination,
                TestContext.Current.CancellationToken);
            Assert.Equal(package.Json, Encoding.UTF8.GetString(bytes));
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_CommitFailurePreservesExistingDestinationAndCleansTemp()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock-MetadataAtomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string original = "reviewed older backup";
            var destination = Path.Combine(
                directory,
                MetadataExportPackage.SuggestedFileName);
            await File.WriteAllTextAsync(
                destination,
                original,
                TestContext.Current.CancellationToken);
            var package = MetadataTransferService.CreateExport(
                CreateSettingsWithCanaries());

            await Assert.ThrowsAsync<IOException>(() =>
                MetadataTransferService.ExportAsync(
                    destination,
                    package,
                    (_, _, _) => throw new IOException("simulated commit failure"),
                    TestContext.Current.CancellationToken));

            Assert.Equal(
                original,
                await File.ReadAllTextAsync(
                    destination,
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                [destination],
                Directory.EnumerateFiles(directory).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UiContract_RequiresAnExactPreviewAndSeparateImportConfirmation()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml"));
        var dialog = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MetadataTransferDialog.xaml"));
        var dialogCode = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MetadataTransferDialog.xaml.cs"));

        Assert.Contains("x:Name=\"MetadataTransferButton\"", mainWindow);
        Assert.Contains(
            "AutomationProperties.Name=\"Export or import safe metadata\"",
            mainWindow);
        Assert.Contains("x:Name=\"ExportPreviewBox\"", dialog);
        Assert.Contains("x:Name=\"ImportPreviewBox\"", dialog);
        Assert.Contains("x:Name=\"ImportConfirmationCheckBox\"", dialog);
        Assert.Contains("x:Name=\"ConfirmImportButton\"", dialog);
        Assert.Contains("IsEnabled=\"False\"", dialog);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", dialog);
        Assert.Contains("ExportPreviewBox.Text = exportPackage.Json", dialogCode);
        Assert.Contains("ImportConfirmationCheckBox.IsChecked == true", dialogCode);
        Assert.DoesNotContain("settings.json", dialogCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WebView", dialogCode, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string> RejectedDocuments => new()
    {
        "not json",
        "{}",
        """
        {
          "format": "sessiondock.metadata",
          "version": 2,
          "accounts": [],
          "publicFavorites": []
        }
        """,
        """
        {
          "format": "sessiondock.metadata",
          "version": 1,
          "unknown": true,
          "accounts": [],
          "publicFavorites": []
        }
        """,
        """
        {
          "format": "sessiondock.metadata",
          "version": 1,
          "version": 1,
          "accounts": [],
          "publicFavorites": []
        }
        """,
        """
        {
          "format": "sessiondock.metadata",
          "version": 1,
          "accounts": [
            { "robloxUserId": 42 },
            { "robloxUserId": 42 }
          ],
          "publicFavorites": []
        }
        """,
        """
        {
          "format": "sessiondock.metadata",
          "version": 1,
          "accounts": [{ "robloxUserId": 42, "color": "#000000" }],
          "publicFavorites": []
        }
        """,
        """
        {
          "format": "sessiondock.metadata",
          "version": 1,
          "accounts": [{ "robloxUserId": 42, "label": " line\nfeed " }],
          "publicFavorites": []
        }
        """,
        """
        {
          "format": "sessiondock.metadata",
          "version": 1,
          "accounts": [],
          "publicFavorites": [
            { "placeId": 12, "accountUserId": 0 },
            { "placeId": 12, "accountUserId": 0 }
          ]
        }
        """
    };

    private static AppSettings CreateSettingsWithCanaries()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = AccountKey,
                    UserId = 42,
                    Username = SensitiveUsername,
                    SessionFolder = SensitiveSessionFolder,
                    Label = "Primary",
                    Group = "Friends",
                    ColorHex = "#7C5CFC",
                    Destination =
                        $"https://www.roblox.com/games/999/Private?privateServerLinkCode={SensitivePrivateCode}"
                }
            ],
            ActiveAccountKey = AccountKey,
            BatchLaunchPresets =
            [
                new BatchLaunchPreset
                {
                    Name = SensitiveIntegration,
                    AccountKeys = [AccountKey],
                    DelaySeconds = 15
                }
            ],
            CustomStartupSoundFileName = SensitiveToken,
            PendingProfileDeletionKeys = [SensitiveToken],
            Destination = SensitiveIntegration,
            LockedUsername = SensitiveToken,
            RecentExperiences =
            [
                new RecentExperience
                {
                    Destination = "12345",
                    PlaceId = 12345,
                    Name = "Public place",
                    CustomName = "Public favorite",
                    IsPinned = true,
                    AccountUserId = 42,
                    AccountUsername = SensitiveUsername,
                    LastLaunchedAt = DateTimeOffset.UtcNow
                },
                new RecentExperience
                {
                    Destination =
                        $"https://www.roblox.com/games/999/Private?privateServerLinkCode={SensitivePrivateCode}",
                    PlaceId = 999,
                    Name = SensitiveToken,
                    IsPrivateServer = true,
                    IsPinned = true,
                    ServerJobId = SensitiveJobId,
                    AccountUserId = 42,
                    AccountUsername = SensitiveUsername,
                    LastLaunchedAt = DateTimeOffset.UtcNow
                }
            ]
        };
        return settings;
    }

    private static AccountProfile CreateAccount(
        string key,
        long userId,
        string username,
        string destination) =>
        new()
        {
            Key = key,
            UserId = userId,
            Username = username,
            SessionFolder = $@"Profiles\{key}",
            Destination = destination
        };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SessionDock.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
