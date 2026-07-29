using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SessionDock.Models;

namespace SessionDock.Services;

internal static class AppDataPaths
{
    internal const string CurrentDirectoryName = "SessionDock";
    internal const string LegacyDirectoryName = "RobloxOne";
    internal const string MigrationConflictFileName = "migration-conflict.txt";
    internal const string MigrationInProgressFileName = "migration-in-progress.txt";
    internal const string LegacyInstallMigrationReceiptFileName =
        "legacy-install-migration.txt";
    internal const string LegacyOptionalDataNoticeFileName =
        "legacy-optional-data-not-copied.txt";
    private const int MaximumMigrationReceiptBytes = 4096;
    private const int MaximumSettingsRecoveryBytes = 4 * 1024 * 1024;
    private static readonly string[] ActiveSettingsFileNames =
        ["settings.json", "settings.backup.json"];
    private static readonly string[] LegacyUserDataFileNames =
    [
        "handlescope.json",
        "profile-cleanup-paused.txt",
        "settings.backup.json",
        "settings.json"
    ];
    private static readonly string[] LegacyUserDataDirectoryNames =
    [
        "PendingProfileDeletions",
        "Profiles",
        "Sounds",
        "WebSession"
    ];
    private static readonly string[] LegacyPreservedSettingsPatterns =
    [
        "settings.corrupt-*.json",
        "settings.backup.corrupt-*.json"
    ];
    private static readonly char[] ReceiptLineSeparators = ['\r', '\n'];
    private static readonly HashSet<string> ActiveMigrationConflicts =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ActiveIncompleteMigrations =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object MigrationConflictLock = new();
    private static readonly object RootConfigurationLock = new();
#if SESSIONDOCK_SMOKE_HARNESS
    private static string? IsolatedRuntimeRoot;
#endif
    private static string? ProtectedInstallRoot;
    private static bool RootResolutionStarted;

    private static readonly Lazy<string> DefaultRoot = new(() =>
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var preferred = Path.Combine(localAppData, CurrentDirectoryName);
        var legacy = Path.Combine(localAppData, LegacyDirectoryName);
        if (ProtectedInstallRoot is not null)
            ValidateInstallRootSeparation(ProtectedInstallRoot, preferred, legacy);

        // RobloxOne was both the historic data directory and Velopack package
        // identity. It remains protected even when the current application is
        // installed side-by-side under the corrected SessionDockApp identity.
        return ResolveForDirectories(
            preferred,
            legacy,
            protectedInstallDirectory: legacy);
    });

    public static string RootDirectory
    {
        get
        {
            lock (RootConfigurationLock)
            {
                RootResolutionStarted = true;
#if SESSIONDOCK_SMOKE_HARNESS
                if (IsolatedRuntimeRoot is not null)
                    return IsolatedRuntimeRoot;
#endif
            }

            return DefaultRoot.Value;
        }
    }

#if SESSIONDOCK_SMOKE_HARNESS
    internal static void ConfigureIsolatedRuntimeRoot(string rootDirectory)
    {
        lock (RootConfigurationLock)
        {
            if (RootResolutionStarted || IsolatedRuntimeRoot is not null)
            {
                throw new InvalidOperationException(
                    "The application data root has already been configured or used.");
            }

            if (!RuntimeSmokeTestOptions.TryValidateRoot(
                    rootDirectory,
                    out var validatedRoot,
                    out _,
                    out var error))
            {
                throw new ArgumentException(error, nameof(rootDirectory));
            }

            Directory.CreateDirectory(validatedRoot!);
            var attributes = File.GetAttributes(validatedRoot!);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateFileSystemEntries(validatedRoot!).Any())
            {
                throw new IOException(
                    "The isolated runtime smoke-test root could not be created safely.");
            }

            // This path intentionally bypasses ResolveForDirectories: a smoke
            // run must never inspect or migrate the user's legacy data root.
            IsolatedRuntimeRoot = validatedRoot;
        }
    }
#endif

    internal static void ConfigureProtectedInstallRoot(string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return;

        var root = Path.GetFullPath(rootDirectory);
        lock (RootConfigurationLock)
        {
            if (RootResolutionStarted)
            {
                throw new InvalidOperationException(
                    "The application data root has already been used.");
            }

            ProtectedInstallRoot = root;
        }
    }

    internal static void ValidateInstallRootSeparation(
        string installDirectory,
        string currentDataDirectory,
        string legacyDataDirectory)
    {
        var install = Path.GetFullPath(installDirectory);
        var current = Path.GetFullPath(currentDataDirectory);
        var legacy = Path.GetFullPath(legacyDataDirectory);
        if (PathsOverlap(install, current) || PathsOverlap(install, legacy))
        {
            throw new InvalidOperationException(
                "The SessionDock installation and user-data directories must be separate.");
        }
    }

    private static bool PathsOverlap(string first, string second) =>
        IsSameOrAncestor(first, second) || IsSameOrAncestor(second, first);

    private static bool IsSameOrAncestor(string parent, string candidate)
    {
        var normalizedParent = parent.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var normalizedCandidate = candidate.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return normalizedCandidate.Equals(
                   normalizedParent,
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(
                   normalizedParent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasMigrationConflict(string directory)
    {
        var root = Path.GetFullPath(directory);
        lock (MigrationConflictLock)
        {
            if (ActiveMigrationConflicts.Contains(root))
                return true;
        }

        var conflictPath = Path.Combine(root, MigrationConflictFileName);
        return File.Exists(conflictPath) || Directory.Exists(conflictPath);
    }

    internal static bool HasIncompleteMigration(string directory)
    {
        var root = Path.GetFullPath(directory);
        lock (MigrationConflictLock)
        {
            if (ActiveIncompleteMigrations.Contains(root))
                return true;
        }

        var markerPath = Path.Combine(root, MigrationInProgressFileName);
        return File.Exists(markerPath) || Directory.Exists(markerPath);
    }

    internal static string ResolveForDirectories(
        string preferredDirectory,
        string legacyDirectory,
        Func<string, FileAttributes>? migrationCompletionProbe = null,
        string? protectedInstallDirectory = null)
    {
        var preferred = Path.GetFullPath(preferredDirectory);
        var legacy = Path.GetFullPath(legacyDirectory);
        if (preferred.Equals(legacy, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The current and legacy data paths must differ.");

        var legacyIsProtectedInstallRoot = IsProtectedInstallRoot(
            legacy,
            protectedInstallDirectory);

        if (Directory.Exists(preferred))
        {
            ThrowIfReparsePoint(preferred);
            if (legacyIsProtectedInstallRoot && !Directory.Exists(legacy))
            {
                try
                {
                    if (IsDefinitelyMissing(legacy, File.GetAttributes))
                    {
                        TryCompleteProtectedMigrationAfterSourceRemoval(preferred);
                        return preferred;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    lock (MigrationConflictLock)
                        ActiveIncompleteMigrations.Add(preferred);
                    return preferred;
                }
            }
            if (IsSafeLegacyDirectory(legacy))
            {
                try
                {
                    if (legacyIsProtectedInstallRoot)
                    {
                        return RecoverFromProtectedInstallRootAndSelect(
                            legacy,
                            preferred);
                    }

                    if (!Directory.EnumerateFileSystemEntries(preferred).Any())
                    {
                        Directory.Delete(preferred, recursive: false);
                        Directory.Move(legacy, preferred);
                    }
                    else if (HasIncompleteMigration(preferred))
                    {
                        // A prior merge stopped after it may have moved data.
                        // Preserve both roots for explicit recovery.
                    }
                    else if (HasSettingsState(preferred) &&
                             HasSettingsState(legacy))
                    {
                        PreserveMigrationConflict(preferred);
                    }
                    else if (TryBeginMigration(preferred))
                    {
                        MergeWithoutOverwrite(legacy, preferred);
                        CompleteMigration(
                            preferred,
                            legacy,
                            migrationCompletionProbe ?? File.GetAttributes);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    if (!Directory.Exists(preferred))
                        return legacy;
                }
            }

            return preferred;
        }

        if (IsSafeLegacyDirectory(legacy))
        {
            try
            {
                if (legacyIsProtectedInstallRoot)
                {
                    Directory.CreateDirectory(preferred);
                    ThrowIfReparsePoint(preferred);
                    return RecoverFromProtectedInstallRootAndSelect(
                        legacy,
                        preferred);
                }

                Directory.Move(legacy, preferred);
                return preferred;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (!Directory.Exists(preferred))
                {
                    if (legacyIsProtectedInstallRoot)
                    {
                        lock (MigrationConflictLock)
                            ActiveIncompleteMigrations.Add(legacy);
                    }
                    return legacy;
                }
            }
        }

        Directory.CreateDirectory(preferred);
        ThrowIfReparsePoint(preferred);
        return preferred;
    }

    private static bool IsProtectedInstallRoot(
        string legacyDirectory,
        string? protectedInstallDirectory)
    {
        if (!string.IsNullOrWhiteSpace(protectedInstallDirectory) &&
            legacyDirectory.Equals(
                Path.GetFullPath(protectedInstallDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The configured Velopack root is authoritative. These markers are a
        // fail-safe for portable recovery builds and older installed layouts
        // where a locator may not be available.
        try
        {
            var names = Directory.EnumerateFileSystemEntries(
                    legacyDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return names.Contains("current") &&
                   (names.Contains("packages") ||
                    names.Contains("Update.exe") ||
                    names.Contains("RobloxOne.exe"));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // If an apparent legacy root cannot be classified, the caller's
            // normal expected-failure handling leaves it untouched.
            return false;
        }
    }

    private static void RecoverFromProtectedInstallRoot(
        string source,
        string destination)
    {
        ThrowIfReparsePoint(source);
        ThrowIfReparsePoint(destination);

        var receiptPath = Path.Combine(
            destination,
            LegacyInstallMigrationReceiptFileName);
        if (TryHandleProtectedMigrationReceipt(
                source,
                destination,
                receiptPath))
        {
            return;
        }

        // There is no completed receipt, so the preserved legacy root is still
        // authoritative. Establish the durable cleanup boundary before the
        // first source settings, profile, or journal read.
        if (!TryBeginMigration(destination))
            throw new IOException("The protected legacy-data migration could not be guarded.");

        // A primary without a trustworthy account boundary, or a pristine
        // explicit empty primary, can conflict with an account-bearing backup.
        // Never guess whether that history represents removal or failed save.
        if (HasAmbiguousAccountHistory(source))
        {
            if (HasPotentialAccountData(source) ||
                HasPotentialAccountData(destination))
            {
                EnsureProfileCleanupPause(destination);
            }
            PreserveMigrationConflict(destination);
            return;
        }

        var sourceEntries = EnumerateLegacyUserDataEntries(source);
        if (sourceEntries.Length == 0)
        {
            CompleteProtectedInstallMigration(destination);
            return;
        }

        if (HasSettingsState(source) &&
            HasSettingsState(destination) &&
            !DestinationSettingsAreRetryCompatible(source, destination))
        {
            if (SourceHasAccountMetadata(source) &&
                IsPristineEmptySettingsState(destination))
            {
                PreservePristineEmptySettingsState(destination);
            }
            else
            {
                PreserveMigrationConflict(destination);
                return;
            }
        }

        var sourceSettingsFingerprint = BuildSettingsFingerprint(source);
        var copiedEntries = new List<string>(sourceEntries.Length);
        foreach (var sourceEntry in sourceEntries)
        {
            var destinationEntry = Path.Combine(
                destination,
                Path.GetFileName(sourceEntry));
            try
            {
                CopyLegacyEntryWithoutOverwrite(sourceEntry, destinationEntry);
                copiedEntries.Add(sourceEntry);
            }
            catch (Exception exception) when (
                IsOptionalLegacyDataEntry(sourceEntry) &&
                exception is IOException or UnauthorizedAccessException)
            {
                // Account metadata and browser sessions are the recovery
                // boundary. A conflicting sound or integration file remains
                // available in the untouched source and must not block it.
                PreserveOptionalDataNotice(destination);
            }
        }

        foreach (var sourceEntry in copiedEntries)
        {
            var destinationEntry = Path.Combine(
                destination,
                Path.GetFileName(sourceEntry));
            if (!DestinationContainsLegacyEntry(sourceEntry, destinationEntry))
            {
                throw new IOException(
                    "Legacy application data changed or could not be verified after copying.");
            }
        }

        var verifiedFingerprint = BuildSettingsFingerprint(source);
        if (!sourceSettingsFingerprint.Equals(
                verifiedFingerprint,
                StringComparison.Ordinal) ||
            !DestinationContainsSettingsFingerprint(
                destination,
                sourceSettingsFingerprint))
        {
            throw new IOException(
                "Legacy settings changed or could not be verified during migration.");
        }

        if (HasSettingsState(source) &&
            !HasLoadableSettingsRevision(destination))
        {
            throw new IOException(
                "Legacy settings were copied but no loadable revision was recovered.");
        }

        var hasPotentialAccountData =
            HasPotentialAccountData(source) ||
            HasPotentialAccountData(destination);
        if (!HasSettingsState(source) &&
            !HasSettingsState(destination) &&
            hasPotentialAccountData)
        {
            // Profiles without matching settings require explicit recovery.
            // Keep the durable guard and the source copy intact.
            return;
        }

        if (hasPotentialAccountData)
            EnsureProfileCleanupPause(destination);

        WriteMigrationReceipt(receiptPath, sourceSettingsFingerprint);
        CompleteProtectedInstallMigration(destination);
    }

    private static bool TryHandleProtectedMigrationReceipt(
        string source,
        string destination,
        string receiptPath)
    {
        if (!File.Exists(receiptPath) && !Directory.Exists(receiptPath))
            return false;

        if (Directory.Exists(receiptPath))
        {
            _ = TryBeginMigration(destination);
            PreserveMigrationConflict(destination);
            return true;
        }

        string? receipt;
        try
        {
            if (!TryReadMigrationReceipt(receiptPath, out receipt) ||
                !IsWellFormedMigrationReceipt(receipt))
            {
                _ = TryBeginMigration(destination);
                PreserveMigrationConflict(destination);
                return true;
            }

            if (!ReceiptMatchesSource(receipt!, source) ||
                HasAmbiguousAccountHistory(source))
            {
                _ = TryBeginMigration(destination);
                PreserveMigrationConflict(destination);
                return true;
            }

            if (StartupWillLoadUsableSettings(
                    destination,
                    out var metadataLessPrimary))
            {
                CompleteProtectedInstallMigration(destination);
                return true;
            }

            // A receipt makes the destination the later authoritative state.
            // If a repair guard cannot be created, keep using that destination
            // with cleanup blocked; never roll back to stale legacy settings.
            if (!TryBeginMigration(destination))
                return true;

            if (metadataLessPrimary)
                PreserveMetadataLessPrimarySettings(destination);

            var restoredFromSource = false;
            if (!StartupWillLoadUsableSettings(destination, out _))
            {
                if (!HasLoadableSettingsRevision(source))
                    return true;

                PreserveUnusableActiveSettings(destination);
                foreach (var fileName in ActiveSettingsFileNames)
                {
                    var sourcePath = Path.Combine(source, fileName);
                    if (File.Exists(sourcePath) || Directory.Exists(sourcePath))
                    {
                        CopyWithoutOverwrite(
                            sourcePath,
                            Path.Combine(destination, fileName));
                    }
                }
                restoredFromSource = true;
            }

            if ((restoredFromSource &&
                 !DestinationContainsSettingsFingerprint(destination, receipt!)) ||
                !StartupWillLoadUsableSettings(destination, out _))
            {
                throw new IOException(
                    "Recovered settings could not be revalidated from the legacy source.");
            }

            if (HasPotentialAccountData(source) ||
                HasPotentialAccountData(destination))
            {
                EnsureProfileCleanupPause(destination);
            }

            CompleteProtectedInstallMigration(destination);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Receipt-backed current data is later than the preserved source.
            // Keep it authoritative while exposing an incomplete-recovery
            // blocker; a later launch can retry when local I/O is available.
            _ = TryBeginMigration(destination);
            return true;
        }
    }

    private static string RecoverFromProtectedInstallRootAndSelect(
        string source,
        string destination)
    {
        try
        {
            RecoverFromProtectedInstallRoot(source, destination);
            return destination;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (HasSettingsState(source) && HasIncompleteMigration(destination))
            {
                lock (MigrationConflictLock)
                    ActiveIncompleteMigrations.Add(source);
                return source;
            }

            return destination;
        }
    }

    private static string[] EnumerateLegacyUserDataEntries(string source)
    {
        var entries = new List<string>();
        foreach (var directoryName in LegacyUserDataDirectoryNames)
        {
            var path = Path.Combine(source, directoryName);
            if (Directory.Exists(path) || File.Exists(path))
                entries.Add(path);
        }

        foreach (var fileName in LegacyUserDataFileNames)
        {
            var path = Path.Combine(source, fileName);
            if (File.Exists(path) || Directory.Exists(path))
                entries.Add(path);
        }

        foreach (var pattern in LegacyPreservedSettingsPatterns)
        {
            entries.AddRange(Directory.EnumerateFileSystemEntries(
                source,
                pattern,
                SearchOption.TopDirectoryOnly));
        }

        return entries
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => IsActiveSettingsFile(Path.GetFileName(path)) ? 1 : 0)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsActiveSettingsFile(string? name) =>
        name is not null &&
        (name.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("settings.backup.json", StringComparison.OrdinalIgnoreCase));

    private static bool IsOptionalLegacyDataEntry(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("Sounds", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("handlescope.json", StringComparison.OrdinalIgnoreCase);
    }

    private static void PreserveOptionalDataNotice(string destination)
    {
        var path = Path.Combine(destination, LegacyOptionalDataNoticeFileName);
        if (File.Exists(path) || Directory.Exists(path))
            return;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream);
            writer.Write(
                "SessionDock recovered the account settings and browser profiles, but a conflicting optional sound or local integration file was left only in the untouched RobloxOne source. Account recovery completed. Keep the legacy source until any optional configuration you still need has been reviewed.");
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Optional-data reporting must not block account recovery.
        }
    }

    private static bool DestinationSettingsAreRetryCompatible(
        string source,
        string destination)
    {
        var destinationHasSettings = false;
        foreach (var fileName in ActiveSettingsFileNames)
        {
            var sourcePath = Path.Combine(source, fileName);
            var destinationPath = Path.Combine(destination, fileName);
            if (!File.Exists(destinationPath))
                continue;

            destinationHasSettings = true;
            if (!File.Exists(sourcePath) || !FilesHaveSameContent(sourcePath, destinationPath))
                return false;
        }

        return destinationHasSettings;
    }

    private static bool SourceHasAccountMetadata(string source)
    {
        foreach (var fileName in ActiveSettingsFileNames)
        {
            if (TryReadSettings(
                    Path.Combine(source, fileName),
                    out var settings) &&
                HasRecoverableAccountMetadata(settings!))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasRecoverableAccountMetadata(AppSettings settings) =>
        settings.Accounts is { Count: > 0 } ||
        settings.LockedUserId is > 0 &&
        !string.IsNullOrWhiteSpace(settings.LockedUsername);

    private static bool HasAmbiguousAccountHistory(string source) =>
        TryReadSettings(
            Path.Combine(source, "settings.json"),
            out var primary,
            out var primaryHasAccountCollection) &&
        !HasRecoverableAccountMetadata(primary!) &&
        (!primaryHasAccountCollection || IsPristineEmptySettings(primary!)) &&
        TryReadSettings(
            Path.Combine(source, "settings.backup.json"),
            out var backup) &&
        HasRecoverableAccountMetadata(backup!);

    private static bool IsUsableSettingsRevision(string path) =>
        TryReadSettings(
            path,
            out var settings,
            out var hasAccountCollection) &&
        (hasAccountCollection || HasRecoverableAccountMetadata(settings!));

    private static bool HasLoadableSettingsRevision(string directory)
    {
        foreach (var fileName in ActiveSettingsFileNames)
        {
            if (IsUsableSettingsRevision(Path.Combine(directory, fileName)))
                return true;
        }
        return false;
    }

    private static bool StartupWillLoadUsableSettings(
        string directory,
        out bool metadataLessPrimary)
    {
        metadataLessPrimary = false;
        var primaryPath = Path.Combine(directory, "settings.json");
        if (File.Exists(primaryPath) &&
            TryReadSettings(
                primaryPath,
                out var primary,
                out var primaryHasAccountCollection))
        {
            if (primaryHasAccountCollection ||
                HasRecoverableAccountMetadata(primary!))
            {
                return true;
            }

            // SettingsService accepts syntactically valid JSON before looking
            // at the backup. Without an account boundary this revision would
            // therefore hide a valid backup instead of falling through to it.
            metadataLessPrimary = true;
            return false;
        }

        return IsUsableSettingsRevision(Path.Combine(
            directory,
            "settings.backup.json"));
    }

    private static bool IsPristineEmptySettingsState(string destination)
    {
        var foundSettings = false;
        foreach (var fileName in ActiveSettingsFileNames)
        {
            var path = Path.Combine(destination, fileName);
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;
            foundSettings = true;
            if (!TryReadSettings(path, out var settings) ||
                !IsPristineEmptySettings(settings!))
            {
                return false;
            }
        }
        return foundSettings;
    }

    private static bool TryReadSettings(string path, out AppSettings? settings) =>
        TryReadSettings(path, out settings, out _);

    private static bool TryReadSettings(
        string path,
        out AppSettings? settings,
        out bool hasAccountCollection)
    {
        settings = null;
        hasAccountCollection = false;
        if (!File.Exists(path))
            return false;
        ThrowIfReparsePoint(path);
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumSettingsRecoveryBytes)
            return false;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var document = JsonDocument.Parse(stream);
            hasAccountCollection =
                document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(
                    nameof(AppSettings.Accounts),
                    out var accountsElement) &&
                accountsElement.ValueKind == JsonValueKind.Array;
            settings = document.RootElement.Deserialize<AppSettings>();
            return settings is not null;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or
                ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private static void PreserveUnusableActiveSettings(string destination)
    {
        foreach (var fileName in ActiveSettingsFileNames)
        {
            var path = Path.Combine(destination, fileName);
            if (Directory.Exists(path))
            {
                throw new IOException(
                    "An active settings path is unexpectedly a directory.");
            }
            if (!File.Exists(path))
                continue;

            ThrowIfReparsePoint(path);
            if (IsUsableSettingsRevision(path))
            {
                throw new IOException(
                    "An active settings file changed while recovery was starting.");
            }

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var preservedPath = Path.Combine(
                destination,
                $"{baseName}.corrupt-before-legacy-recovery-{Guid.NewGuid():N}.json");
            File.Move(path, preservedPath);
        }
    }

    private static void PreserveMetadataLessPrimarySettings(string destination)
    {
        var path = Path.Combine(destination, "settings.json");
        if (!File.Exists(path) ||
            !TryReadSettings(
                path,
                out var settings,
                out var hasAccountCollection) ||
            hasAccountCollection ||
            HasRecoverableAccountMetadata(settings!))
        {
            throw new IOException(
                "The metadata-less primary settings changed before recovery.");
        }

        ThrowIfReparsePoint(path);
        var preservedPath = Path.Combine(
            destination,
            $"settings.corrupt-before-legacy-recovery-{Guid.NewGuid():N}.json");
        File.Move(path, preservedPath);
    }

    private static bool IsPristineEmptySettings(AppSettings settings) =>
        settings.Accounts is { Count: 0 } &&
        settings.ActiveAccountKey is null &&
        settings.RecentExperiences is { Count: 0 } &&
        settings.BatchLaunchPresets is { Count: 0 } &&
        settings.BatchLaunchDelaySeconds == 8 &&
        settings.MainWindowPlacement is null &&
        settings.UiSoundsEnabled &&
        !settings.UseLightTheme &&
        LocalizationPreference.System.Equals(
            settings.Language,
            StringComparison.Ordinal) &&
        "soft".Equals(settings.StartupSound, StringComparison.Ordinal) &&
        settings.CustomStartupSoundFileName is null &&
        settings.PendingProfileDeletionKeys is { Count: 0 } &&
        settings.LockedUserId is null &&
        settings.LockedUsername is null &&
        settings.PlaceId is null &&
        settings.Destination is null;

    private static void PreservePristineEmptySettingsState(string destination)
    {
        foreach (var fileName in ActiveSettingsFileNames)
        {
            var path = Path.Combine(destination, fileName);
            if (!File.Exists(path))
                continue;
            ThrowIfReparsePoint(path);
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var preservedPath = Path.Combine(
                destination,
                $"{baseName}.corrupt-empty-before-legacy-recovery-{Guid.NewGuid():N}.json");
            File.Move(path, preservedPath);
        }
    }

    private static void CopyWithoutOverwrite(string source, string destination)
    {
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Legacy application data contains a reparse point.");

        if ((attributes & FileAttributes.Directory) != 0)
        {
            if (File.Exists(destination))
                throw new IOException("Legacy application data conflicts with a current file.");
            if (Directory.Exists(destination))
            {
                if (!EntriesHaveSameContent(source, destination))
                {
                    throw new IOException(
                        "Legacy application data conflicts with a current directory.");
                }
                return;
            }

            var temporaryDirectory =
                $"{destination}.migration-{Guid.NewGuid():N}.tmp";
            try
            {
                CopyDirectoryToNew(source, temporaryDirectory);
                if (!EntriesHaveSameContent(source, temporaryDirectory))
                {
                    throw new IOException(
                        "Copied legacy application data could not be verified.");
                }
                Directory.Move(temporaryDirectory, destination);
                return;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(temporaryDirectory))
                        Directory.Delete(temporaryDirectory, recursive: true);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // A uniquely named staging tree is never treated as live data.
                }
            }
        }

        if (Directory.Exists(destination))
            throw new IOException("Legacy application data conflicts with a current directory.");
        if (File.Exists(destination))
        {
            ThrowIfReparsePoint(destination);
            if (!FilesHaveSameContent(source, destination))
                throw new IOException("Legacy application data conflicts with current data.");
            return;
        }

        var temporaryPath = $"{destination}.migration-{Guid.NewGuid():N}.tmp";
        try
        {
            using (var input = new FileStream(
                       source,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 128 * 1024,
                       FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            if (!FilesHaveSameContent(source, temporaryPath))
                throw new IOException("Copied legacy application data could not be verified.");
            File.Move(temporaryPath, destination);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A uniquely named temporary copy is never treated as live data.
            }
        }
    }

    private static void CopyLegacyEntryWithoutOverwrite(
        string source,
        string destination)
    {
        if (Path.GetFileName(source).Equals(
                "Profiles",
                StringComparison.OrdinalIgnoreCase))
        {
            CopyProfilesWithoutOverwrite(source, destination);
            return;
        }
        CopyWithoutOverwrite(source, destination);
    }

    private static void CopyProfilesWithoutOverwrite(
        string source,
        string destination)
    {
        var sourceAttributes = File.GetAttributes(source);
        if ((sourceAttributes & FileAttributes.Directory) == 0 ||
            (sourceAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Legacy account profiles are not a safe directory.");
        }
        if (File.Exists(destination))
            throw new IOException("Legacy account profiles conflict with a current file.");
        if (!Directory.Exists(destination))
        {
            CopyWithoutOverwrite(source, destination);
            return;
        }
        ThrowIfReparsePoint(destination);

        var sourceChildren = Directory.EnumerateFileSystemEntries(
                source,
                "*",
                SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Preflight every collision before adding any missing profile. A
        // divergent WebView profile is never merged file by file.
        foreach (var child in sourceChildren)
        {
            ValidateEntryTree(child);
            var destinationChild = Path.Combine(
                destination,
                Path.GetFileName(child));
            if ((File.Exists(destinationChild) || Directory.Exists(destinationChild)) &&
                !EntriesHaveSameContent(child, destinationChild))
            {
                throw new IOException(
                    "A legacy browser profile conflicts with a current profile.");
            }
        }

        foreach (var child in sourceChildren)
        {
            var destinationChild = Path.Combine(
                destination,
                Path.GetFileName(child));
            if (!File.Exists(destinationChild) && !Directory.Exists(destinationChild))
                CopyWithoutOverwrite(child, destinationChild);
        }
    }

    private static bool DestinationContainsLegacyEntry(
        string source,
        string destination)
    {
        if (!Path.GetFileName(source).Equals(
                "Profiles",
                StringComparison.OrdinalIgnoreCase))
        {
            return EntriesHaveSameContent(source, destination);
        }
        if (!Directory.Exists(destination))
            return false;
        ThrowIfReparsePoint(source);
        ThrowIfReparsePoint(destination);
        foreach (var child in Directory.EnumerateFileSystemEntries(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var destinationChild = Path.Combine(
                destination,
                Path.GetFileName(child));
            if ((!File.Exists(destinationChild) && !Directory.Exists(destinationChild)) ||
                !EntriesHaveSameContent(child, destinationChild))
            {
                return false;
            }
        }
        return true;
    }

    private static void ValidateEntryTree(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Application data contains a reparse point.");
        if ((attributes & FileAttributes.Directory) == 0)
            return;
        foreach (var child in Directory.EnumerateFileSystemEntries(
                     path,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            ValidateEntryTree(child);
        }
    }

    private static void CopyDirectoryToNew(string source, string destination)
    {
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Legacy application data contains an unsafe directory.");
        }
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Legacy application data staging already exists.");

        Directory.CreateDirectory(destination);
        foreach (var child in Directory.EnumerateFileSystemEntries(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var childAttributes = File.GetAttributes(child);
            if ((childAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Legacy application data contains a reparse point.");
            }

            var childDestination = Path.Combine(
                destination,
                Path.GetFileName(child));
            if ((childAttributes & FileAttributes.Directory) != 0)
            {
                CopyDirectoryToNew(child, childDestination);
            }
            else
            {
                CopyFileToNew(child, childDestination);
            }
        }
    }

    private static void CopyFileToNew(string source, string destination)
    {
        var attributes = File.GetAttributes(source);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Legacy application data contains an unsafe file.");
        using (var input = new FileStream(
                   source,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        using (var output = new FileStream(
                   destination,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read,
                   bufferSize: 128 * 1024,
                   FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
        if (!FilesHaveSameContent(source, destination))
            throw new IOException("Copied legacy application data could not be verified.");
    }

    private static bool EntriesHaveSameContent(string source, string destination)
    {
        var sourceAttributes = File.GetAttributes(source);
        var destinationAttributes = File.GetAttributes(destination);
        if ((sourceAttributes & FileAttributes.ReparsePoint) != 0 ||
            (destinationAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Application data contains a reparse point.");
        }

        var sourceIsDirectory =
            (sourceAttributes & FileAttributes.Directory) != 0;
        var destinationIsDirectory =
            (destinationAttributes & FileAttributes.Directory) != 0;
        if (sourceIsDirectory != destinationIsDirectory)
            return false;
        if (!sourceIsDirectory)
            return FilesHaveSameContent(source, destination);

        var sourceChildren = Directory.EnumerateFileSystemEntries(
                source,
                "*",
                SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var destinationChildren = Directory.EnumerateFileSystemEntries(
                destination,
                "*",
                SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceChildren.Length != destinationChildren.Length)
            return false;
        for (var index = 0; index < sourceChildren.Length; index++)
        {
            if (!Path.GetFileName(sourceChildren[index]).Equals(
                    Path.GetFileName(destinationChildren[index]),
                    StringComparison.OrdinalIgnoreCase) ||
                !EntriesHaveSameContent(
                    sourceChildren[index],
                    destinationChildren[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool FilesHaveSameContent(string first, string second)
    {
        ThrowIfReparsePoint(first);
        ThrowIfReparsePoint(second);
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        if (firstInfo.Length != secondInfo.Length)
            return false;

        using var firstStream = new FileStream(
            first,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var secondStream = new FileStream(
            second,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        Span<byte> firstHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> secondHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(firstStream, firstHash);
        SHA256.HashData(secondStream, secondHash);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static string BuildSettingsFingerprint(string directory)
    {
        var builder = new StringBuilder();
        builder.AppendLine("schema=1");
        foreach (var fileName in ActiveSettingsFileNames)
        {
            var path = Path.Combine(directory, fileName);
            builder.Append(fileName).Append('=');
            if (File.Exists(path))
            {
                ThrowIfReparsePoint(path);
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                builder.Append(Convert.ToHexString(SHA256.HashData(stream)));
            }
            else
            {
                builder.Append("missing");
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static bool DestinationContainsSettingsFingerprint(
        string destination,
        string sourceFingerprint)
    {
        foreach (var line in sourceFingerprint.Split(
                     ReceiptLineSeparators,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || line[..separator].Equals("schema", StringComparison.Ordinal))
                continue;
            var fileName = line[..separator];
            var expected = line[(separator + 1)..];
            var path = Path.Combine(destination, fileName);
            if (expected.Equals("missing", StringComparison.Ordinal))
                continue;

            if (!File.Exists(path))
                return false;
            ThrowIfReparsePoint(path);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(
                    expected,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasPotentialAccountData(string directory) =>
        Directory.Exists(Path.Combine(directory, "Profiles")) ||
        Directory.Exists(Path.Combine(directory, "WebSession"));

    private static void EnsureProfileCleanupPause(string destination)
    {
        var path = Path.Combine(destination, "profile-cleanup-paused.txt");
        if (File.Exists(path) || Directory.Exists(path))
            return;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream);
            writer.Write(
                "SessionDock copied account browser profiles out of the legacy RobloxOne installation directory. Automatic orphan-profile cleanup is paused so recovered sessions remain available for validation. Remove this file only after every expected account and sign-in has been confirmed.");
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw;
        }
    }

    private static void WriteMigrationReceipt(string path, string content)
    {
        if (File.Exists(path))
        {
            ThrowIfReparsePoint(path);
            if (!File.ReadAllText(path, Encoding.UTF8).Equals(
                    content,
                    StringComparison.Ordinal))
            {
                throw new IOException("The legacy migration receipt conflicts with this migration.");
            }
            return;
        }

        if (Directory.Exists(path))
            throw new IOException("The legacy migration receipt path is not a file.");
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A uniquely named temporary receipt is never trusted.
            }
        }
    }

    private static bool TryReadMigrationReceipt(string path, out string? receipt)
    {
        receipt = null;
        if (!File.Exists(path))
            return false;
        ThrowIfReparsePoint(path);
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumMigrationReceiptBytes)
            return true;
        receipt = File.ReadAllText(path, Encoding.UTF8);
        return true;
    }

    private static bool ReceiptMatchesSource(string receipt, string source)
    {
        if (!IsWellFormedMigrationReceipt(receipt))
            return false;
        if (!HasSettingsState(source))
            return true;
        return receipt.Equals(BuildSettingsFingerprint(source), StringComparison.Ordinal);
    }

    private static bool IsWellFormedMigrationReceipt(string? receipt) =>
        !string.IsNullOrEmpty(receipt) &&
        (receipt.StartsWith("schema=1\n", StringComparison.Ordinal) ||
         receipt.StartsWith("schema=1\r\n", StringComparison.Ordinal));

    private static void TryCompleteProtectedMigrationAfterSourceRemoval(
        string destination)
    {
        try
        {
            var receiptPath = Path.Combine(
                destination,
                LegacyInstallMigrationReceiptFileName);
            if (HasSettingsState(destination) &&
                TryReadMigrationReceipt(receiptPath, out var receipt) &&
                IsWellFormedMigrationReceipt(receipt))
            {
                CompleteProtectedInstallMigration(destination);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // An unverifiable completion leaves the cleanup guard in place.
        }
    }

    private static void CompleteProtectedInstallMigration(string destination) =>
        RemoveMigrationGuard(destination);

    private static bool HasSettingsState(string directory) =>
        File.Exists(Path.Combine(directory, "settings.json")) ||
        File.Exists(Path.Combine(directory, "settings.backup.json"));

    private static bool TryBeginMigration(string preferredDirectory)
    {
        lock (MigrationConflictLock)
            ActiveIncompleteMigrations.Add(preferredDirectory);

        var markerPath = Path.Combine(
            preferredDirectory,
            MigrationInProgressFileName);
        if (File.Exists(markerPath))
            return true;
        if (Directory.Exists(markerPath))
            return false;
        try
        {
            using var stream = new FileStream(
                markerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(
                "SessionDock began recovering legacy RobloxOne data, but the migration did not finish cleanly. Some files may already be in the SessionDock directory. The legacy source was preserved and automatic browser-profile cleanup is paused. Reconcile both data directories before removing this file.");
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A concurrent creator may have made the same durable guard.
            return File.Exists(markerPath);
        }
    }

    private static void CompleteMigration(
        string preferredDirectory,
        string legacyDirectory,
        Func<string, FileAttributes> getAttributes)
    {
        if (!IsDefinitelyMissing(legacyDirectory, getAttributes))
            return;

        RemoveMigrationGuard(preferredDirectory);
    }

    private static void RemoveMigrationGuard(string preferredDirectory)
    {
        var markerPath = Path.Combine(
            preferredDirectory,
            MigrationInProgressFileName);
        try
        {
            File.Delete(markerPath);
            if (File.Exists(markerPath) || Directory.Exists(markerPath))
                return;

            lock (MigrationConflictLock)
                ActiveIncompleteMigrations.Remove(preferredDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A stale guard pauses cleanup safely until explicit recovery.
        }
    }

    private static bool IsDefinitelyMissing(
        string path,
        Func<string, FileAttributes> getAttributes)
    {
        try
        {
            _ = getAttributes(path);
            return false;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static void PreserveMigrationConflict(string preferredDirectory)
    {
        lock (MigrationConflictLock)
        {
            ActiveMigrationConflicts.Add(preferredDirectory);
        }

        var conflictPath = Path.Combine(
            preferredDirectory,
            MigrationConflictFileName);
        try
        {
            using var stream = new FileStream(
                conflictPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(
                "SessionDock found independent or conflicting data in the current SessionDock data directory and the legacy RobloxOne location. No conflicting legacy files were changed. Automatic browser-profile cleanup is paused until the legacy data has been recovered or intentionally preserved elsewhere. Remove this file only after resolving the legacy data.");
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The untouched legacy directory remains the authoritative fallback.
        }
    }

    private static void MergeWithoutOverwrite(string source, string destination)
    {
        ThrowIfReparsePoint(source);
        ThrowIfReparsePoint(destination);
        var entries = Directory.EnumerateFileSystemEntries(source)
            .OrderBy(path => Path.GetFileName(path).Equals(
                "settings.json",
                StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToArray();
        foreach (var sourceEntry in entries)
        {
            if ((File.GetAttributes(sourceEntry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Legacy application data contains a reparse point.");

            var destinationEntry = Path.Combine(
                destination,
                Path.GetFileName(sourceEntry));
            if (Directory.Exists(sourceEntry))
            {
                if (!Directory.Exists(destinationEntry) &&
                    !File.Exists(destinationEntry))
                {
                    Directory.Move(sourceEntry, destinationEntry);
                }
                else if (Directory.Exists(destinationEntry))
                {
                    MergeWithoutOverwrite(sourceEntry, destinationEntry);
                }

                continue;
            }

            if (!File.Exists(destinationEntry) &&
                !Directory.Exists(destinationEntry))
            {
                File.Move(sourceEntry, destinationEntry);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(source).Any())
            Directory.Delete(source, recursive: false);
    }

    private static bool IsSafeLegacyDirectory(string path)
    {
        if (!Directory.Exists(path))
            return false;
        ThrowIfReparsePoint(path);
        return true;
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The application data directory cannot be a reparse point.");
    }
}
