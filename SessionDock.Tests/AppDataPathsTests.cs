using System.Text.Json;
using SessionDock.Models;
using SessionDock.ReleaseTrust;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AppDataPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-path-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ResolveForDirectories_MovesLegacyDataToSessionDock()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "saved-data");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);

        Assert.Equal(preferred, resolved);
        Assert.False(Directory.Exists(legacy));
        Assert.Equal(
            "saved-data",
            File.ReadAllText(Path.Combine(preferred, "settings.json")));
    }

    [Fact]
    public void ResolveForDirectories_PrefersExistingSessionDockData()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        Directory.CreateDirectory(preferred);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(preferred, "settings.json"), "current");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);

        Assert.Equal(preferred, resolved);
        Assert.Equal(
            "current",
            File.ReadAllText(Path.Combine(preferred, "settings.json")));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void ResolveForDirectories_MergesLegacyDataWithoutOverwritingCurrentFiles()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        Directory.CreateDirectory(preferred);
        Directory.CreateDirectory(Path.Combine(legacy, "Profiles", "account"));
        File.WriteAllText(Path.Combine(preferred, "handlescope.json"), "current");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy-settings");
        File.WriteAllText(
            Path.Combine(legacy, "Profiles", "account", "Cookies"),
            "session");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);

        Assert.Equal(preferred, resolved);
        Assert.False(Directory.Exists(legacy));
        Assert.Equal(
            "current",
            File.ReadAllText(Path.Combine(preferred, "handlescope.json")));
        Assert.Equal(
            "legacy-settings",
            File.ReadAllText(Path.Combine(preferred, "settings.json")));
        Assert.Equal(
            "session",
            File.ReadAllText(Path.Combine(
                preferred,
                "Profiles",
                "account",
                "Cookies")));
    }

    [Fact]
    public void ResolveForDirectories_DualSettingsRoots_PreservesLegacyProfileDuringCleanup()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var currentKey = Guid.NewGuid().ToString("N");
        var legacyKey = Guid.NewGuid().ToString("N");
        var currentProfile = Path.Combine(preferred, "Profiles", currentKey);
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(currentProfile);
        Directory.CreateDirectory(legacyProfile);
        WriteSettings(preferred, currentKey, 101, "current-user");
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var sentinel = Path.Combine(legacyProfile, "Cookies");
        File.WriteAllText(sentinel, "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(preferred, resolved);
        Assert.Equal(currentKey, Assert.Single(loaded.Accounts).Key);
        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        var loadNotice = Assert.IsType<string>(settingsService.LoadNotice);
        Assert.Contains(
            "RobloxOne",
            loadNotice,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationConflictFileName)));
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(preferred, "Profiles", legacyKey)));
    }

    [Fact]
    public void ResolveForDirectories_PreexistingPartialMigration_PreservesMovedLegacyProfile()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var currentKey = Guid.NewGuid().ToString("N");
        var legacyKey = Guid.NewGuid().ToString("N");
        var movedLegacyProfile = Path.Combine(preferred, "Profiles", legacyKey);
        Directory.CreateDirectory(movedLegacyProfile);
        Directory.CreateDirectory(legacy);
        WriteSettings(preferred, currentKey, 101, "current-user");
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var sentinel = Path.Combine(movedLegacyProfile, "Cookies");
        File.WriteAllText(sentinel, "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
    }

    [Fact]
    public void ResolveForDirectories_MergeFailureAfterProfileMove_PausesCleanup()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var legacyKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(preferred);
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        File.WriteAllText(Path.Combine(preferred, "handlescope.json"), "current");
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var sentinelName = "Cookies";
        File.WriteAllText(
            Path.Combine(legacyProfile, sentinelName),
            "legacy-session");
        using var settingsLock = new FileStream(
            Path.Combine(legacy, "settings.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.Contains(
            "did not finish cleanly",
            Assert.IsType<string>(settingsService.LoadNotice),
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            "Profiles",
            legacyKey,
            sentinelName)));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
    }

    [Fact]
    public void ResolveForDirectories_UnverifiableMigrationCompletion_KeepsGuard()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var legacyKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(preferred);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(preferred, "handlescope.json"), "current");
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        File.WriteAllText(Path.Combine(legacyProfile, "Cookies"), "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            _ => throw new UnauthorizedAccessException(
                "The legacy root could not be positively inspected."));
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            "Profiles",
            legacyKey,
            "Cookies")));
    }

    [Fact]
    public void ResolveForDirectories_OneSettingsRoot_MigratesReferencedLegacyProfile()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var legacyKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(preferred);
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        File.WriteAllText(Path.Combine(preferred, "handlescope.json"), "current");
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var sentinel = Path.Combine(legacyProfile, "Cookies");
        File.WriteAllText(sentinel, "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.True(settingsService.CanReconcileProfiles);
        Assert.Equal(legacyKey, Assert.Single(loaded.Accounts).Key);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            "Profiles",
            legacyKey,
            "Cookies")));
        Assert.False(Directory.Exists(legacy));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationConflictFileName)));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
    }

    [Theory]
    [InlineData("settings.json", "settings.backup.json")]
    [InlineData("settings.backup.json", "settings.json")]
    public void ResolveForDirectories_PrimaryOrBackupInBothRoots_PreservesLegacyData(
        string preferredSettingsFile,
        string legacySettingsFile)
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var currentKey = Guid.NewGuid().ToString("N");
        var legacyKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(preferred, "Profiles", currentKey));
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        WriteSettings(
            preferred,
            currentKey,
            101,
            "current-user",
            preferredSettingsFile);
        WriteSettings(
            legacy,
            legacyKey,
            202,
            "legacy-user",
            legacySettingsFile);
        var sentinel = Path.Combine(legacyProfile, "Cookies");
        File.WriteAllText(sentinel, "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(preferred, legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(currentKey, Assert.Single(loaded.Accounts).Key);
        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(Path.Combine(legacy, legacySettingsFile)));
    }

    [Fact]
    public void ResolveForDirectories_InstalledLegacyRootWithoutAccountData_LeavesInstallerUntouched()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var currentKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(preferred, "Profiles", currentKey));
        WriteSettings(preferred, currentKey, 101, "recovered-user");
        CreateVelopackInstallLayout(legacy);
        Directory.CreateDirectory(Path.Combine(legacy, "Sounds"));
        File.WriteAllText(
            Path.Combine(legacy, "Sounds", "ui-v1.wav"),
            "new-install-sound");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var loaded = new SettingsService(resolved).Load();

        Assert.Equal(preferred, resolved);
        Assert.Equal(currentKey, Assert.Single(loaded.Accounts).Key);
        AssertVelopackInstallLayoutIsIntact(legacy);
        Assert.False(Directory.Exists(Path.Combine(preferred, "current")));
        Assert.False(Directory.Exists(Path.Combine(preferred, "packages")));
        Assert.False(File.Exists(Path.Combine(preferred, "Update.exe")));
        Assert.False(File.Exists(Path.Combine(preferred, "RobloxOne.exe")));
    }

    [Fact]
    public void ResolveForDirectories_InstalledLegacyRoot_CopiesOnlyUserDataAndPreservesSource()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var legacyKey = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        File.WriteAllText(Path.Combine(legacyProfile, "Cookies"), "session");
        Directory.CreateDirectory(Path.Combine(legacy, "Sounds"));
        File.WriteAllText(
            Path.Combine(legacy, "Sounds", "custom.wav"),
            "sound");
        File.WriteAllText(Path.Combine(legacy, "handlescope.json"), "configuration");
        File.WriteAllText(Path.Combine(legacy, "not-user-data.txt"), "leave-behind");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(preferred, resolved);
        Assert.Equal(legacyKey, Assert.Single(loaded.Accounts).Key);
        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.Equal(
            "session",
            File.ReadAllText(Path.Combine(
                preferred,
                "Profiles",
                legacyKey,
                "Cookies")));
        Assert.Equal(
            "sound",
            File.ReadAllText(Path.Combine(preferred, "Sounds", "custom.wav")));
        Assert.Equal(
            "configuration",
            File.ReadAllText(Path.Combine(preferred, "handlescope.json")));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.LegacyInstallMigrationReceiptFileName)));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            "profile-cleanup-paused.txt")));
        Assert.False(File.Exists(Path.Combine(preferred, "not-user-data.txt")));
        Assert.False(Directory.Exists(Path.Combine(preferred, "current")));
        Assert.False(Directory.Exists(Path.Combine(preferred, "packages")));
        Assert.False(File.Exists(Path.Combine(preferred, "Update.exe")));
        Assert.False(File.Exists(Path.Combine(preferred, "RobloxOne.exe")));
        AssertVelopackInstallLayoutIsIntact(legacy);
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
        Assert.True(File.Exists(Path.Combine(legacyProfile, "Cookies")));
        Assert.True(File.Exists(Path.Combine(legacy, "not-user-data.txt")));

        var resolvedAgain = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var secondService = new SettingsService(resolvedAgain);
        var secondLoad = secondService.Load();
        var secondCleanup = secondService.CleanupOrphanedSessionDirectories(
            secondLoad,
            TestContext.Current.CancellationToken);

        Assert.Equal(preferred, resolvedAgain);
        Assert.Equal(legacyKey, Assert.Single(secondLoad.Accounts).Key);
        Assert.Equal(0, secondCleanup);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            "Profiles",
            legacyKey,
            "Cookies")));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationConflictFileName)));
        AssertVelopackInstallLayoutIsIntact(legacy);
    }

    [Fact]
    public void ResolveForDirectories_InstalledLegacyRootWithConflictingSettings_PreservesBothRoots()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var currentKey = Guid.NewGuid().ToString("N");
        var legacyKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(preferred, "Profiles", currentKey));
        WriteSettings(preferred, currentKey, 101, "current-user");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        var sentinel = Path.Combine(legacyProfile, "Cookies");
        File.WriteAllText(sentinel, "legacy-session");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();
        var removed = settingsService.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(currentKey, Assert.Single(loaded.Accounts).Key);
        Assert.Equal(0, removed);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationConflictFileName)));
        Assert.False(Directory.Exists(Path.Combine(preferred, "Profiles", legacyKey)));
        Assert.True(File.Exists(sentinel));
        AssertVelopackInstallLayoutIsIntact(legacy);
    }

    [Fact]
    public void ResolveForDirectories_InstalledLegacyRootCopyFailure_PreservesSourceAndCanResume()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var legacyKey = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(preferred);
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, legacyKey, 202, "legacy-user");
        var legacyProfile = Path.Combine(legacy, "Profiles", legacyKey);
        Directory.CreateDirectory(legacyProfile);
        var sentinel = Path.Combine(legacyProfile, "Cookies");
        File.WriteAllText(sentinel, "legacy-session");
        var lockedRecoveryFile = Path.Combine(
            legacy,
            "settings.corrupt-locked.json");
        File.WriteAllText(lockedRecoveryFile, "preserved-diagnostic");

        using (new FileStream(
                   lockedRecoveryFile,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var interruptedResolution = AppDataPaths.ResolveForDirectories(
                preferred,
                legacy,
                protectedInstallDirectory: legacy);
            var interruptedService = new SettingsService(interruptedResolution);
            var interruptedSettings = interruptedService.Load();
            var removed = interruptedService.CleanupOrphanedSessionDirectories(
                interruptedSettings,
                TestContext.Current.CancellationToken);

            Assert.Equal(legacy, interruptedResolution);
            Assert.Equal(legacyKey, Assert.Single(interruptedSettings.Accounts).Key);
            Assert.Equal(0, removed);
            Assert.False(interruptedService.CanReconcileProfiles);
            interruptedService.Save(interruptedSettings);
            Assert.True(File.Exists(Path.Combine(
                preferred,
                AppDataPaths.MigrationInProgressFileName)));
            Assert.Equal(
                "legacy-session",
                File.ReadAllText(Path.Combine(
                    preferred,
                    "Profiles",
                    legacyKey,
                    "Cookies")));
            Assert.True(File.Exists(sentinel));
            Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
            AssertVelopackInstallLayoutIsIntact(legacy);
        }

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var settingsService = new SettingsService(resolved);
        var loaded = settingsService.Load();

        Assert.Equal(legacyKey, Assert.Single(loaded.Accounts).Key);
        Assert.False(settingsService.CanReconcileProfiles);
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.LegacyInstallMigrationReceiptFileName)));
        Assert.True(File.Exists(sentinel));
        AssertVelopackInstallLayoutIsIntact(legacy);
    }

    [Fact]
    public void ResolveForDirectories_PristineEmptyCurrentSettings_RecoversLegacyAccountsAndSplitProfiles()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var firstKey = Guid.NewGuid().ToString("N");
        var secondKey = Guid.NewGuid().ToString("N");
        var destinationOnlyProfile = Path.Combine(preferred, "Profiles", secondKey);
        Directory.CreateDirectory(destinationOnlyProfile);
        File.WriteAllText(
            Path.Combine(destinationOnlyProfile, "Cookies"),
            "second-session");
        Directory.CreateDirectory(preferred);
        File.WriteAllText(
            Path.Combine(preferred, "settings.json"),
            JsonSerializer.Serialize(new AppSettings()));
        CreateVelopackInstallLayout(legacy);
        WriteSettings(
            legacy,
            [
                CreateAccount(firstKey, 101, "first-user"),
                CreateAccount(secondKey, 202, "second-user")
            ],
            firstKey);
        var sourceProfile = Path.Combine(legacy, "Profiles", firstKey);
        Directory.CreateDirectory(sourceProfile);
        File.WriteAllText(Path.Combine(sourceProfile, "Cookies"), "first-session");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();
        var removed = service.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(preferred, resolved);
        Assert.Equal(2, loaded.Accounts.Count);
        Assert.Contains(loaded.Accounts, account => account.Key == firstKey);
        Assert.Contains(loaded.Accounts, account => account.Key == secondKey);
        Assert.Equal(firstKey, loaded.ActiveAccountKey);
        Assert.Equal(0, removed);
        Assert.False(service.CanReconcileProfiles);
        Assert.Equal(
            "first-session",
            File.ReadAllText(Path.Combine(
                preferred,
                "Profiles",
                firstKey,
                "Cookies")));
        Assert.Equal(
            "second-session",
            File.ReadAllText(Path.Combine(
                preferred,
                "Profiles",
                secondKey,
                "Cookies")));
        Assert.Single(Directory.EnumerateFiles(
            preferred,
            "settings.corrupt-empty-before-legacy-recovery-*.json"));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
        Assert.True(File.Exists(Path.Combine(sourceProfile, "Cookies")));
    }

    [Fact]
    public void ResolveForDirectories_DestinationOnlyProfile_PausesCleanupAfterSettingsRecovery()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var accountKey = Guid.NewGuid().ToString("N");
        var destinationOnlyKey = Guid.NewGuid().ToString("N");
        var destinationOnlyProfile = Path.Combine(
            preferred,
            "Profiles",
            destinationOnlyKey);
        Directory.CreateDirectory(destinationOnlyProfile);
        var sentinel = Path.Combine(destinationOnlyProfile, "Cookies");
        File.WriteAllText(sentinel, "destination-only-session");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, accountKey, 101, "legacy-user");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();
        var removed = service.CleanupOrphanedSessionDirectories(
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(accountKey, Assert.Single(loaded.Accounts).Key);
        Assert.Equal(0, removed);
        Assert.False(service.CanReconcileProfiles);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            "profile-cleanup-paused.txt")));
    }

    [Fact]
    public void ResolveForDirectories_ConflictingProfileTree_DoesNotPartiallyMerge()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        var currentProfile = Path.Combine(preferred, "Profiles", key);
        Directory.CreateDirectory(currentProfile);
        File.WriteAllText(Path.Combine(currentProfile, "Z-conflict"), "current");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, key, 101, "legacy-user");
        var legacyProfile = Path.Combine(legacy, "Profiles", key);
        Directory.CreateDirectory(legacyProfile);
        File.WriteAllText(Path.Combine(legacyProfile, "A-added"), "must-not-copy");
        File.WriteAllText(Path.Combine(legacyProfile, "Z-conflict"), "legacy");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);

        Assert.Equal(legacy, resolved);
        Assert.False(File.Exists(Path.Combine(currentProfile, "A-added")));
        Assert.Equal(
            "current",
            File.ReadAllText(Path.Combine(currentProfile, "Z-conflict")));
        Assert.True(File.Exists(Path.Combine(legacyProfile, "A-added")));
        Assert.Equal(
            "legacy",
            File.ReadAllText(Path.Combine(legacyProfile, "Z-conflict")));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
    }

    [Fact]
    public void ResolveForDirectories_LightThemePreference_IsNotTreatedAsPristine()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        Directory.CreateDirectory(preferred);
        File.WriteAllText(
            Path.Combine(preferred, "settings.json"),
            JsonSerializer.Serialize(new AppSettings
            {
                UseLightTheme = true
            }));
        CreateVelopackInstallLayout(legacy);
        WriteSettings(
            legacy,
            Guid.NewGuid().ToString("N"),
            101,
            "legacy-user");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();

        Assert.Equal(preferred, resolved);
        Assert.True(loaded.UseLightTheme);
        Assert.Empty(loaded.Accounts);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationConflictFileName)));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
    }

    [Theory]
    [InlineData("batch-delay")]
    [InlineData("window-placement")]
    public void ResolveForDirectories_NewPreferences_AreNotTreatedAsPristine(
        string preference)
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var current = new AppSettings();
        if (preference == "batch-delay")
        {
            current.BatchLaunchDelaySeconds = 15;
        }
        else
        {
            current.MainWindowPlacement = new WindowPlacementSettings
            {
                MonitorDeviceName = @"\\.\DISPLAY2",
                OffsetX = 120,
                OffsetY = 80,
                Width = 1080,
                Height = 720,
                IsMaximized = true
            };
        }

        Directory.CreateDirectory(preferred);
        File.WriteAllText(
            Path.Combine(preferred, "settings.json"),
            JsonSerializer.Serialize(current));
        CreateVelopackInstallLayout(legacy);
        WriteSettings(
            legacy,
            Guid.NewGuid().ToString("N"),
            101,
            "legacy-user");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var loaded = new SettingsService(resolved).Load();

        Assert.Equal(preferred, resolved);
        Assert.Empty(loaded.Accounts);
        if (preference == "batch-delay")
            Assert.Equal(15, loaded.BatchLaunchDelaySeconds);
        else
            Assert.NotNull(loaded.MainWindowPlacement);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationConflictFileName)));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
    }

    [Fact]
    public void ResolveForDirectories_MatchingReceiptWithMissingDestinationSettings_RestoresSettings()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, key, 101, "legacy-user");
        Directory.CreateDirectory(Path.Combine(legacy, "Profiles", key));

        _ = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        File.Delete(Path.Combine(preferred, "settings.json"));
        var backupPath = Path.Combine(preferred, "settings.backup.json");
        if (File.Exists(backupPath))
            File.Delete(backupPath);

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();

        Assert.Equal(preferred, resolved);
        Assert.Equal(key, Assert.Single(loaded.Accounts).Key);
        Assert.True(File.Exists(Path.Combine(preferred, "settings.json")));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
    }

    [Fact]
    public void ResolveForDirectories_ReceiptAndRemovedSource_ClearsStaleMigrationGuard()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, key, 101, "legacy-user");

        _ = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        File.WriteAllText(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName), "simulated crash");
        Directory.Delete(legacy, recursive: true);

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);

        Assert.Equal(preferred, resolved);
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
        Assert.Equal(
            key,
            Assert.Single(new SettingsService(resolved).Load().Accounts).Key);
    }

    [Fact]
    public void ResolveForDirectories_OptionalFileConflict_DoesNotBlockAccountRecovery()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(preferred);
        File.WriteAllText(Path.Combine(preferred, "handlescope.json"), "current");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, key, 101, "legacy-user");
        File.WriteAllText(Path.Combine(legacy, "handlescope.json"), "legacy");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();

        Assert.Equal(preferred, resolved);
        Assert.Equal(key, Assert.Single(loaded.Accounts).Key);
        Assert.Equal("current", File.ReadAllText(Path.Combine(
            preferred,
            "handlescope.json")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(
            legacy,
            "handlescope.json")));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.LegacyInstallMigrationReceiptFileName)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.LegacyOptionalDataNoticeFileName)));
        Assert.Contains(
            "optional",
            Assert.IsType<string>(service.LoadNotice),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveForDirectories_EmptyLegacyPrimaryWithAccountBackup_FailsClosedAsConflict()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(preferred);
        File.WriteAllText(
            Path.Combine(preferred, "settings.json"),
            JsonSerializer.Serialize(new AppSettings()));
        CreateVelopackInstallLayout(legacy);
        File.WriteAllText(
            Path.Combine(legacy, "settings.json"),
            JsonSerializer.Serialize(new AppSettings()));
        WriteSettings(
            legacy,
            key,
            101,
            "backup-user",
            fileName: "settings.backup.json");
        var sourceProfile = Path.Combine(legacy, "Profiles", key);
        Directory.CreateDirectory(sourceProfile);
        File.WriteAllText(Path.Combine(sourceProfile, "Cookies"), "signed-in");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var resolvedAgain = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolvedAgain);
        var loaded = service.Load();

        Assert.Equal(preferred, resolved);
        Assert.Equal(preferred, resolvedAgain);
        Assert.Empty(loaded.Accounts);
        Assert.False(service.CanReconcileProfiles);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationConflictFileName)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.LegacyInstallMigrationReceiptFileName)));
        Assert.False(Directory.Exists(Path.Combine(preferred, "Profiles", key)));
        Assert.True(File.Exists(Path.Combine(sourceProfile, "Cookies")));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.backup.json")));
    }

    [Fact]
    public void ResolveForDirectories_MetadataLessLegacyPrimaryWithAccountBackup_FailsClosedAsConflict()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        File.WriteAllText(
            Path.Combine(legacy, "settings.json"),
            "{\"UiSoundsEnabled\":false}");
        WriteSettings(
            legacy,
            key,
            101,
            "backup-user",
            fileName: "settings.backup.json");
        var sourceProfile = Path.Combine(legacy, "Profiles", key);
        Directory.CreateDirectory(sourceProfile);
        File.WriteAllText(Path.Combine(sourceProfile, "Cookies"), "signed-in");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();

        Assert.Equal(preferred, resolved);
        Assert.Empty(loaded.Accounts);
        Assert.False(service.CanReconcileProfiles);
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationConflictFileName)));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.LegacyInstallMigrationReceiptFileName)));
        Assert.True(File.Exists(Path.Combine(sourceProfile, "Cookies")));
    }

    [Fact]
    public void ResolveForDirectories_PristineCurrentSettings_RecoversLegacyLockedAccount()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        Directory.CreateDirectory(preferred);
        File.WriteAllText(
            Path.Combine(preferred, "settings.json"),
            JsonSerializer.Serialize(new AppSettings()));
        CreateVelopackInstallLayout(legacy);
        File.WriteAllText(
            Path.Combine(legacy, "settings.json"),
            JsonSerializer.Serialize(new AppSettings
            {
                LockedUserId = 101,
                LockedUsername = "legacy-user"
            }));
        var sourceSession = Path.Combine(legacy, "WebSession");
        Directory.CreateDirectory(sourceSession);
        File.WriteAllText(Path.Combine(sourceSession, "Cookies"), "signed-in");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();

        var account = Assert.Single(loaded.Accounts);
        Assert.Equal("legacy", account.Key);
        Assert.Equal(101, account.UserId);
        Assert.Equal("legacy-user", account.Username);
        Assert.Equal("WebSession", account.SessionFolder);
        Assert.Equal("signed-in", File.ReadAllText(Path.Combine(
            preferred,
            "WebSession",
            "Cookies")));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.LegacyInstallMigrationReceiptFileName)));
        Assert.False(service.CanReconcileProfiles);
    }

    [Fact]
    public void ResolveForDirectories_MatchingReceiptWithCorruptDestinationSettings_RestoresSource()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, key, 101, "legacy-user");

        _ = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        File.WriteAllText(Path.Combine(preferred, "settings.json"), "{not-json");
        var backupPath = Path.Combine(preferred, "settings.backup.json");
        if (File.Exists(backupPath))
            File.Delete(backupPath);

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();

        Assert.Equal(preferred, resolved);
        Assert.Equal(key, Assert.Single(loaded.Accounts).Key);
        Assert.Single(Directory.EnumerateFiles(
            preferred,
            "settings.corrupt-before-legacy-recovery-*.json"));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
    }

    [Fact]
    public void ResolveForDirectories_MatchingReceiptWithMetadataLessDestination_RestoresSource()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, key, 101, "legacy-user");

        _ = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        File.WriteAllText(Path.Combine(preferred, "settings.json"), "{}");
        var backupPath = Path.Combine(preferred, "settings.backup.json");
        if (File.Exists(backupPath))
            File.Delete(backupPath);

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var loaded = new SettingsService(resolved).Load();

        Assert.Equal(key, Assert.Single(loaded.Accounts).Key);
        Assert.Equal(
            "{}",
            File.ReadAllText(Assert.Single(Directory.EnumerateFiles(
                preferred,
            "settings.corrupt-before-legacy-recovery-*.json"))));
    }

    [Fact]
    public void ResolveForDirectories_MetadataLessPrimaryWithValidLocalBackup_UsesBackup()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, key, 101, "legacy-user");

        _ = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        File.Copy(
            Path.Combine(legacy, "settings.json"),
            Path.Combine(preferred, "settings.backup.json"),
            overwrite: true);
        File.WriteAllText(Path.Combine(preferred, "settings.json"), "{}");

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var loaded = new SettingsService(resolved).Load();

        Assert.Equal(preferred, resolved);
        Assert.Equal(key, Assert.Single(loaded.Accounts).Key);
        Assert.Equal(
            "{}",
            File.ReadAllText(Assert.Single(Directory.EnumerateFiles(
                preferred,
                "settings.corrupt-before-legacy-recovery-*.json"))));
    }

    [Fact]
    public void ResolveForDirectories_MatchingReceiptWithValidEmptyDestination_DoesNotResurrectAccount()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, key, 101, "legacy-user");

        _ = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        File.WriteAllText(
            Path.Combine(preferred, "settings.json"),
            JsonSerializer.Serialize(new AppSettings()));
        var backupPath = Path.Combine(preferred, "settings.backup.json");
        File.Copy(
            Path.Combine(legacy, "settings.json"),
            backupPath,
            overwrite: true);

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var loaded = new SettingsService(resolved).Load();

        Assert.Equal(preferred, resolved);
        Assert.Empty(loaded.Accounts);
        Assert.Empty(Directory.EnumerateFiles(
            preferred,
            "settings.corrupt-before-legacy-recovery-*.json"));
        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public void ResolveForDirectories_ReceiptWithObstructedGuard_KeepsLaterEmptyDestination()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, key, 101, "legacy-user");

        _ = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        File.Copy(
            Path.Combine(legacy, "settings.json"),
            Path.Combine(preferred, "settings.backup.json"),
            overwrite: true);
        File.WriteAllText(
            Path.Combine(preferred, "settings.json"),
            JsonSerializer.Serialize(new AppSettings()));
        Directory.CreateDirectory(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName));

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();

        Assert.Equal(preferred, resolved);
        Assert.Empty(loaded.Accounts);
        Assert.False(service.CanReconcileProfiles);
        Assert.Equal(
            key,
            Assert.Single(new SettingsService(legacy).Load().Accounts).Key);
        Assert.True(Directory.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
    }

    [Fact]
    public void ResolveForDirectories_ReceiptWithLockedSource_KeepsHealthyDestination()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, key, 101, "legacy-user");
        _ = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);

        using (new FileStream(
                   Path.Combine(legacy, "settings.json"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var resolved = AppDataPaths.ResolveForDirectories(
                preferred,
                legacy,
                protectedInstallDirectory: legacy);
            var service = new SettingsService(resolved);
            var loaded = service.Load();

            Assert.Equal(preferred, resolved);
            Assert.Equal(key, Assert.Single(loaded.Accounts).Key);
            Assert.False(service.CanReconcileProfiles);
        }

        var resolvedAgain = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        Assert.Equal(preferred, resolvedAgain);
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
    }

    [Fact]
    public async Task ResolveForDirectories_PendingDeletionJournal_MigratesWithRemovalIntent()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WritePendingDeletionState(legacy, key);

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();
        var deleted = await service.DeletePendingProfileOnceAsync(
            key,
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(preferred, resolved);
        Assert.Contains(key, loaded.PendingProfileDeletionKeys);
        Assert.Contains(key, service.GetJournaledProfileDeletionKeys());
        Assert.True(deleted);
        Assert.False(Directory.Exists(Path.Combine(preferred, "Profiles", key)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.LegacyInstallMigrationReceiptFileName)));
        Assert.True(File.Exists(Path.Combine(
            legacy,
            "PendingProfileDeletions",
            $"{key}.delete")));
    }

    [Fact]
    public async Task ResolveForDirectories_ConflictingPendingDeletionJournal_UsesUntouchedSource()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var key = Guid.NewGuid().ToString("N");
        var otherKey = Guid.NewGuid().ToString("N");
        CreateVelopackInstallLayout(legacy);
        WritePendingDeletionState(legacy, key);
        var preferredJournal = Path.Combine(preferred, "PendingProfileDeletions");
        Directory.CreateDirectory(preferredJournal);
        File.WriteAllText(Path.Combine(preferredJournal, $"{otherKey}.delete"), otherKey);

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();
        var deleted = await service.DeletePendingProfileOnceAsync(
            key,
            loaded,
            TestContext.Current.CancellationToken);

        Assert.Equal(legacy, resolved);
        Assert.False(service.CanReconcileProfiles);
        Assert.Contains(key, loaded.PendingProfileDeletionKeys);
        Assert.True(deleted);
        Assert.False(Directory.Exists(Path.Combine(legacy, "Profiles", key)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            "PendingProfileDeletions",
            $"{otherKey}.delete")));
        Assert.False(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.LegacyInstallMigrationReceiptFileName)));
        Assert.True(File.Exists(Path.Combine(
            preferred,
            AppDataPaths.MigrationInProgressFileName)));
    }

    [Fact]
    public void ResolveForDirectories_LockedSourceSettings_GuardsCleanupBeforeReadingSource()
    {
        var preferred = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");
        var sourceKey = Guid.NewGuid().ToString("N");
        var destinationOnlyKey = Guid.NewGuid().ToString("N");
        var sentinel = Path.Combine(
            preferred,
            "Profiles",
            destinationOnlyKey,
            "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "preserve-me");
        File.WriteAllText(
            Path.Combine(preferred, "settings.json"),
            JsonSerializer.Serialize(new AppSettings()));
        CreateVelopackInstallLayout(legacy);
        WriteSettings(legacy, sourceKey, 101, "legacy-user");
        var sourceProfile = Path.Combine(legacy, "Profiles", sourceKey);
        Directory.CreateDirectory(sourceProfile);
        File.WriteAllText(Path.Combine(sourceProfile, "Cookies"), "signed-in");

        using (new FileStream(
                   Path.Combine(legacy, "settings.json"),
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var interruptedRoot = AppDataPaths.ResolveForDirectories(
                preferred,
                legacy,
                protectedInstallDirectory: legacy);
            var interruptedService = new SettingsService(interruptedRoot);
            var interruptedSettings = interruptedService.Load();
            var removed = interruptedService.CleanupOrphanedSessionDirectories(
                interruptedSettings,
                TestContext.Current.CancellationToken);

            Assert.Equal(legacy, interruptedRoot);
            Assert.False(interruptedService.CanReconcileProfiles);
            Assert.Equal(0, removed);
            Assert.True(File.Exists(sentinel));
            Assert.True(File.Exists(Path.Combine(
                preferred,
                AppDataPaths.MigrationInProgressFileName)));
        }

        var resolved = AppDataPaths.ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
        var service = new SettingsService(resolved);
        var loaded = service.Load();

        Assert.Equal(preferred, resolved);
        Assert.Equal(sourceKey, Assert.Single(loaded.Accounts).Key);
        Assert.False(service.CanReconcileProfiles);
        Assert.True(File.Exists(sentinel));
        Assert.True(File.Exists(Path.Combine(sourceProfile, "Cookies")));
    }

    [Fact]
    public void ValidateInstallRootSeparation_RejectsAncestorAndDescendantCollisions()
    {
        var current = Path.Combine(_root, "SessionDock");
        var legacy = Path.Combine(_root, "RobloxOne");

        Assert.Throws<InvalidOperationException>(() =>
            AppDataPaths.ValidateInstallRootSeparation(_root, current, legacy));
        Assert.Throws<InvalidOperationException>(() =>
            AppDataPaths.ValidateInstallRootSeparation(
                Path.Combine(current, "application"),
                current,
                legacy));

        AppDataPaths.ValidateInstallRootSeparation(
            Path.Combine(_root, "SessionDockApp"),
            current,
            legacy);
    }

    [Fact]
    public void ResolveForDirectories_RejectsTheSamePathForBothIdentities()
    {
        var path = Path.Combine(_root, "SessionDock");

        Assert.Throws<ArgumentException>(() =>
            AppDataPaths.ResolveForDirectories(path, path));
    }

    [Fact]
    public void ReleaseInstallerIdentity_DoesNotCollideWithEitherDataRoot()
    {
        Assert.False(AppDataPaths.CurrentDirectoryName.Equals(
            ReleaseDescriptorPolicy.VelopackPackageId,
            StringComparison.OrdinalIgnoreCase));
        Assert.False(AppDataPaths.LegacyDirectoryName.Equals(
            ReleaseDescriptorPolicy.VelopackPackageId,
            StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void WriteSettings(
        string root,
        string key,
        long userId,
        string username,
        string fileName = "settings.json")
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = key,
                    UserId = userId,
                    Username = username,
                    SessionFolder = $@"Profiles\{key}"
                }
            ],
            ActiveAccountKey = key
        };
        File.WriteAllText(
            Path.Combine(root, fileName),
            JsonSerializer.Serialize(settings));
    }

    private static void WriteSettings(
        string root,
        List<AccountProfile> accounts,
        string activeAccountKey)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "settings.json"),
            JsonSerializer.Serialize(new AppSettings
            {
                Accounts = accounts,
                ActiveAccountKey = activeAccountKey
            }));
    }

    private static AccountProfile CreateAccount(
        string key,
        long userId,
        string username) =>
        new()
        {
            Key = key,
            UserId = userId,
            Username = username,
            SessionFolder = $@"Profiles\{key}"
        };

    private static void WritePendingDeletionState(string root, string key)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "settings.json"),
            JsonSerializer.Serialize(new AppSettings
            {
                PendingProfileDeletionKeys = [key]
            }));
        var profile = Path.Combine(root, "Profiles", key);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "Cookies"), "remove-me");
        var journal = Path.Combine(root, "PendingProfileDeletions");
        Directory.CreateDirectory(journal);
        File.WriteAllText(Path.Combine(journal, $"{key}.delete"), key);
    }

    private static void CreateVelopackInstallLayout(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "current"));
        Directory.CreateDirectory(Path.Combine(root, "packages"));
        File.WriteAllText(Path.Combine(root, "current", "sq.version"), "metadata");
        File.WriteAllText(Path.Combine(root, "Update.exe"), "updater");
        File.WriteAllText(Path.Combine(root, "RobloxOne.exe"), "stub");
    }

    private static void AssertVelopackInstallLayoutIsIntact(string root)
    {
        Assert.Equal(
            "metadata",
            File.ReadAllText(Path.Combine(root, "current", "sq.version")));
        Assert.True(Directory.Exists(Path.Combine(root, "packages")));
        Assert.Equal("updater", File.ReadAllText(Path.Combine(root, "Update.exe")));
        Assert.Equal("stub", File.ReadAllText(Path.Combine(root, "RobloxOne.exe")));
    }
}
