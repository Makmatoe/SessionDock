using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SessionDock.Models;

namespace SessionDock.Services;

public sealed class SettingsService
{
    private enum PathProbeResult
    {
        Missing,
        File,
        Directory,
        Uncertain
    }

    private const int MaximumSettingsBytes = 4 * 1024 * 1024;
    private const int MaximumRecoveryNoticeStateBytes = 64 * 1024;
    internal const int MaximumPendingProfileDeletions = 256;
    private const string ProfileDeletionMarkerExtension = ".delete";
    internal const string RecoveryNoticeAcknowledgementFileName =
        "recovery-notice.ack";
    public static readonly IReadOnlyList<string> AccountColors =
        ["#7C5CFC", "#4D8DFF", "#27B58A", "#E0A33A", "#E36B8D", "#A56DE2"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _rootDirectory;
    private readonly string _backupPath;
    private readonly string _profileCleanupGuardPath;
    private readonly string _profileDeletionJournalDirectory;
    private readonly string _recoveryNoticeAcknowledgementPath;
    private readonly string _settingsPath;
    private readonly object _saveLock = new();
    private readonly Func<string, FileAttributes> _getAttributes;
    private readonly Func<string, CancellationToken, bool> _deleteDirectoryTree;
    private bool _primaryIsUnreadable;
    private bool _profileCleanupGuardRequiredButUnavailable;
    private string? _loadNoticeFingerprint;

    public SettingsService(string? storageDirectory = null)
        : this(
            storageDirectory,
            File.GetAttributes,
            TryDeleteDirectoryTree)
    {
    }

    internal SettingsService(
        string? storageDirectory,
        Func<string, FileAttributes> getAttributes)
        : this(storageDirectory, getAttributes, TryDeleteDirectoryTree)
    {
    }

    internal SettingsService(
        string? storageDirectory,
        Func<string, FileAttributes> getAttributes,
        Func<string, CancellationToken, bool> deleteDirectoryTree)
    {
        _getAttributes = getAttributes ??
            throw new ArgumentNullException(nameof(getAttributes));
        _deleteDirectoryTree = deleteDirectoryTree ??
            throw new ArgumentNullException(nameof(deleteDirectoryTree));
        _rootDirectory = Path.GetFullPath(
            storageDirectory ?? AppDataPaths.RootDirectory);
        Directory.CreateDirectory(_rootDirectory);
        ThrowIfReparsePoint(_rootDirectory);
        _settingsPath = Path.Combine(_rootDirectory, "settings.json");
        _backupPath = Path.Combine(_rootDirectory, "settings.backup.json");
        _profileCleanupGuardPath = Path.Combine(
            _rootDirectory,
            "profile-cleanup-paused.txt");
        _profileDeletionJournalDirectory = Path.Combine(
            _rootDirectory,
            "PendingProfileDeletions");
        _recoveryNoticeAcknowledgementPath = Path.Combine(
            _rootDirectory,
            RecoveryNoticeAcknowledgementFileName);
        RefreshProfileCleanupState();
    }

    public string? LoadNotice { get; private set; }
    public bool CanReconcileProfiles { get; private set; } = true;

    internal void AcknowledgeLoadNotice()
    {
        var fingerprint = _loadNoticeFingerprint;
        if (string.IsNullOrWhiteSpace(fingerprint))
            return;

        lock (_saveLock)
        {
            try
            {
                var state = ProbePath(_recoveryNoticeAcknowledgementPath);
                if (state == PathProbeResult.Directory)
                {
                    throw new IOException(
                        "The recovery-notice acknowledgement path is a directory.");
                }
                if (state == PathProbeResult.File)
                    ThrowIfReparsePoint(_recoveryNoticeAcknowledgementPath);
                File.WriteAllText(
                    _recoveryNoticeAcknowledgementPath,
                    fingerprint,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _loadNoticeFingerprint = null;
            }
            catch (Exception exception) when (
                LocalDataException.IsExpectedPersistenceFailure(exception))
            {
                // A failed acknowledgement is harmless: the warning remains
                // visible on the next start instead of weakening recovery.
                System.Diagnostics.Trace.WriteLine(
                    $"Recovery notice acknowledgement failed: {exception.GetType().Name}.");
            }
        }
    }

    public string GetSessionDataDirectory(AccountProfile profile)
    {
        var path = Path.GetFullPath(Path.Combine(_rootDirectory, profile.SessionFolder));
        if (!path.StartsWith(
                _rootDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The account session path is outside SessionDock.");
        }
        EnsureDirectoryPathHasNoReparsePoints(path);
        return path;
    }

    public AppSettings Load()
    {
        LoadNotice = null;
        _loadNoticeFingerprint = null;
        RefreshProfileCleanupState();
        _primaryIsUnreadable = false;
        var primaryState = ProbePath(_settingsPath);
        var backupState = ProbePath(_backupPath);
        var primaryExists = primaryState != PathProbeResult.Missing;
        if (primaryState == PathProbeResult.Missing &&
            backupState == PathProbeResult.Missing)
        {
            if (HasPreservedSettingsFiles() || HasPotentialAccountProfiles())
            {
                RequireProfileCleanupPause();
            }
            AddProfileCleanupNotice();
            return new();
        }

        if (primaryState != PathProbeResult.Missing &&
            TryLoadFile(
                _settingsPath,
                out var settings,
                out var migrated,
                out var accountMetadataWasDiscarded))
        {
            var metadataSupportsProfileCleanup =
                !accountMetadataWasDiscarded &&
                BackupMetadataAllowsProfileCleanup(settings, backupState);
            var cleanupPauseIsDurable = metadataSupportsProfileCleanup ||
                RequireProfileCleanupPause();
            if (migrated && cleanupPauseIsDurable)
            {
                try
                {
                    Save(settings);
                }
                catch (Exception exception) when (
                    LocalDataException.IsExpectedPersistenceFailure(exception))
                {
                    // Keep the successfully loaded settings in memory for this run.
                    System.Diagnostics.Trace.WriteLine(
                        $"Migrated settings could not be persisted: {exception.GetType().Name}.");
                }
            }
            AddProfileCleanupNotice();
            return settings;
        }

        if (backupState != PathProbeResult.Missing &&
            TryLoadFile(
                _backupPath,
                out settings,
                out migrated,
                out _))
        {
            // The backup is intentionally the prior successful revision. A
            // newer account can therefore be absent even when it validates.
            var cleanupPauseIsDurable = RequireProfileCleanupPause();
            _primaryIsUnreadable = true;
            if (cleanupPauseIsDurable &&
                PreserveUnreadableFile(_settingsPath))
            {
                _primaryIsUnreadable = false;
                try
                {
                    WriteFresh(settings);
                    if (migrated)
                        Save(settings);
                }
                catch (Exception exception) when (
                    LocalDataException.IsExpectedPersistenceFailure(exception))
                {
                    // The validated backup remains available if restoration is blocked.
                    System.Diagnostics.Trace.WriteLine(
                        $"Recovered settings could not be restored: {exception.GetType().Name}.");
                }
            }
            LoadNotice = primaryExists
                ? "SessionDock recovered your accounts and history from the local settings backup. The unreadable file was preserved for diagnosis."
                : "SessionDock recovered your accounts and history from the local settings backup after the primary file was missing.";
            AddProfileCleanupNotice();
            return settings;
        }

        _primaryIsUnreadable = !PreserveUnreadableFile(_settingsPath);
        PreserveUnreadableFile(_backupPath);
        RequireProfileCleanupPause();
        LoadNotice =
            "SessionDock could not read the local settings or its backup. The unreadable files were preserved, and browser profiles were left untouched.";
        AddProfileCleanupNotice();
        return new();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_saveLock)
        {
            if (_profileCleanupGuardRequiredButUnavailable &&
                !RequireProfileCleanupPause())
            {
                throw new IOException(
                    "Profile cleanup could not be paused durably, so settings were not overwritten.");
            }
            _profileCleanupGuardRequiredButUnavailable = false;

            if (_primaryIsUnreadable)
            {
                if (!PreserveUnreadableFile(_settingsPath))
                {
                    throw new IOException(
                        "The unreadable settings file is still in use and was not overwritten.");
                }
                _primaryIsUnreadable = false;
            }

            var temporaryPath = CreateTemporaryPath("save");
            try
            {
                WriteSettingsFile(temporaryPath, settings);
                var primaryExists = ValidateFilePathAndCheckExists(
                    _settingsPath);
                _ = ValidateFilePathAndCheckExists(_backupPath);
                if (primaryExists)
                {
                    File.Replace(
                        temporaryPath,
                        _settingsPath,
                        _backupPath,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, _settingsPath);
                }
            }
            finally
            {
                DeleteTemporaryFileBestEffort(temporaryPath);
            }
        }
    }

    public int CleanupOrphanedSessionDirectories(AppSettings settings) =>
        CleanupOrphanedSessionDirectories(
            settings,
            CancellationToken.None);

    internal int CleanupOrphanedSessionDirectories(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanReconcileProfiles)
            return 0;

        var removed = 0;
        try
        {
            var profilesDirectory = Path.Combine(_rootDirectory, "Profiles");
            FileAttributes profileAttributes;
            try
            {
                profileAttributes = File.GetAttributes(profilesDirectory);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or
                    DirectoryNotFoundException)
            {
                return 0;
            }
            if ((profileAttributes & FileAttributes.Directory) == 0 ||
                (profileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                RequireProfileCleanupPause();
                AddProfileCleanupNotice();
                return 0;
            }

            var referencedKeys = settings.Accounts
                .Select(account => account.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in Directory.EnumerateDirectories(
                         profilesDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Path.GetFileName(directory);
                if (!IsValidKey(key) || key == "legacy" ||
                    referencedKeys.Contains(key) || IsReparsePoint(directory))
                {
                    continue;
                }
                try
                {
                    if (_deleteDirectoryTree(
                            directory,
                            cancellationToken))
                        removed++;
                }
                catch (Exception exception) when (
                    LocalDataException.IsExpectedPersistenceFailure(exception))
                {
                    // A WebView2 process can briefly retain an interrupted profile.
                }
            }
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            RequireProfileCleanupPause();
            AddProfileCleanupNotice();
        }

        return removed;
    }

    internal void StageProfileDeletion(string accountKey)
    {
        if (!TryGetCanonicalSessionFolder(accountKey, out _))
            throw new ArgumentException(
                "The account key cannot identify a browser profile.",
                nameof(accountKey));

        lock (_saveLock)
        {
            EnsureDirectoryPathHasNoReparsePoints(
                _profileDeletionJournalDirectory);
            var markerPath = GetProfileDeletionMarkerPath(accountKey);
            var markerState = ProbePath(markerPath);
            if (markerState == PathProbeResult.File)
            {
                ValidateProfileDeletionMarker(markerPath);
                return;
            }
            if (markerState != PathProbeResult.Missing)
            {
                throw new IOException(
                    "The account-removal marker path is unavailable.");
            }

            var existingKeys = GetJournaledProfileDeletionKeysCore();
            if (existingKeys.Count >= MaximumPendingProfileDeletions)
            {
                throw new IOException(
                    "Too many account removals are already pending.");
            }

            var temporaryPath = Path.Combine(
                _profileDeletionJournalDirectory,
                $"pending-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, markerPath);
            }
            finally
            {
                DeleteTemporaryFileBestEffort(temporaryPath);
            }
        }
    }

    internal IReadOnlyList<string> GetJournaledProfileDeletionKeys()
    {
        lock (_saveLock)
            return GetJournaledProfileDeletionKeysCore();
    }

    internal bool ClearProfileDeletionJournal(string accountKey)
    {
        if (!TryGetCanonicalSessionFolder(accountKey, out _))
            return false;

        lock (_saveLock)
        {
            var markerPath = GetProfileDeletionMarkerPath(accountKey);
            try
            {
                var journalState = ProbePath(
                    _profileDeletionJournalDirectory);
                if (journalState == PathProbeResult.Missing)
                    return true;
                if (journalState != PathProbeResult.Directory)
                    return false;
                ThrowIfReparsePoint(_profileDeletionJournalDirectory);
                var state = ProbePath(markerPath);
                if (state == PathProbeResult.Missing)
                    return true;
                if (state != PathProbeResult.File)
                    return false;
                ValidateProfileDeletionMarker(markerPath);
                File.Delete(markerPath);
                return ProbePath(markerPath) == PathProbeResult.Missing;
            }
            catch (Exception exception) when (
                LocalDataException.IsExpectedPersistenceFailure(exception))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Account-removal marker cleanup failed: {exception.GetType().Name}.");
                return false;
            }
        }
    }

    private IReadOnlyList<string> GetJournaledProfileDeletionKeysCore()
    {
        var state = ProbePath(_profileDeletionJournalDirectory);
        if (state == PathProbeResult.Missing)
            return [];
        if (state != PathProbeResult.Directory)
        {
            throw new IOException(
                "The account-removal journal cannot be inspected safely.");
        }
        ThrowIfReparsePoint(_profileDeletionJournalDirectory);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspected = 0;
        foreach (var markerPath in Directory.EnumerateFiles(
                     _profileDeletionJournalDirectory,
                     $"*{ProfileDeletionMarkerExtension}",
                     SearchOption.TopDirectoryOnly))
        {
            if (++inspected > MaximumPendingProfileDeletions * 2)
                break;
            var fileName = Path.GetFileName(markerPath);
            var accountKey = fileName[..^ProfileDeletionMarkerExtension.Length];
            if (!TryGetCanonicalSessionFolder(accountKey, out _) ||
                keys.Count >= MaximumPendingProfileDeletions)
            {
                continue;
            }

            try
            {
                ValidateProfileDeletionMarker(markerPath);
                keys.Add(accountKey);
            }
            catch (Exception exception) when (
                LocalDataException.IsExpectedPersistenceFailure(exception))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Ignored an invalid account-removal marker: {exception.GetType().Name}.");
            }
        }
        return keys.ToList();
    }

    private string GetProfileDeletionMarkerPath(string accountKey) =>
        Path.Combine(
            _profileDeletionJournalDirectory,
            $"{accountKey}{ProfileDeletionMarkerExtension}");

    private void ValidateProfileDeletionMarker(string markerPath)
    {
        if (!ValidateFilePathAndCheckExists(markerPath))
            throw new FileNotFoundException(
                "The account-removal marker is missing.",
                markerPath);
    }

    private bool IsProfileDeletionJournaled(string accountKey)
    {
        lock (_saveLock)
        {
            try
            {
                if (ProbePath(_profileDeletionJournalDirectory) !=
                    PathProbeResult.Directory)
                {
                    return false;
                }
                ThrowIfReparsePoint(_profileDeletionJournalDirectory);
                var markerPath = GetProfileDeletionMarkerPath(accountKey);
                ValidateProfileDeletionMarker(markerPath);
                return true;
            }
            catch (Exception exception) when (
                LocalDataException.IsExpectedPersistenceFailure(exception))
            {
                return false;
            }
        }
    }

    internal ImportedSoundRetention CaptureImportedSoundRetention(
        string? liveFileName)
    {
        lock (_saveLock)
        {
            var retained = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            AddImportedSoundReference(retained, liveFileName);

            foreach (var path in new[] { _settingsPath, _backupPath })
            {
                var state = ProbePath(path);
                if (state == PathProbeResult.Missing)
                    continue;
                if (state != PathProbeResult.File ||
                    !TryLoadFile(path, out var settings, out _, out _))
                {
                    return new ImportedSoundRetention(
                        CanReconcile: false,
                        ReferencesAreComplete: false,
                        retained);
                }

                AddImportedSoundReference(
                    retained,
                    settings.CustomStartupSoundFileName);
            }

            return new ImportedSoundRetention(
                CanReconcileProfiles,
                ReferencesAreComplete: true,
                retained);
        }
    }

    internal async Task<bool> DeletePendingProfileAsync(
        string accountKey,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        return await DeletePendingProfileCoreAsync(
            accountKey,
            settings,
            maximumAttempts: 10,
            cancellationToken);
    }

    internal async Task<bool> DeletePendingProfileOnceAsync(
        string accountKey,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        return await DeletePendingProfileCoreAsync(
            accountKey,
            settings,
            maximumAttempts: 1,
            cancellationToken);
    }

    private async Task<bool> DeletePendingProfileCoreAsync(
        string accountKey,
        AppSettings settings,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryGetCanonicalSessionFolder(accountKey, out var sessionFolder) ||
            !IsProfileDeletionJournaled(accountKey) ||
            !settings.PendingProfileDeletionKeys.Any(key =>
                string.Equals(
                    key,
                    accountKey,
                    StringComparison.OrdinalIgnoreCase)) ||
            settings.Accounts.Any(account =>
                account.Key.Equals(
                    accountKey,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    account.SessionFolder,
                    sessionFolder,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return await DeleteSessionDataAsync(
            new AccountProfile
            {
                Key = accountKey,
                SessionFolder = sessionFolder
            },
            maximumAttempts,
            cancellationToken);
    }

    public Task<bool> DeleteSessionDataAsync(
        AccountProfile profile,
        CancellationToken cancellationToken = default) =>
        DeleteSessionDataAsync(
            profile,
            maximumAttempts: 10,
            cancellationToken);

    private async Task<bool> DeleteSessionDataAsync(
        AccountProfile profile,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumAttempts,
            1);
        if (!TryGetCanonicalSessionFolder(
                profile.Key,
                out var canonicalSessionFolder) ||
            !string.Equals(
                profile.SessionFolder,
                canonicalSessionFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path;
        try
        {
            path = GetSessionDataDirectoryForDeletion(profile);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
                InvalidOperationException)
        {
            return false;
        }

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await Task.Run(
                    () =>
                    {
                        var pathState = ProbePath(path);
                        if (pathState == PathProbeResult.Missing)
                            return true;
                        if (pathState != PathProbeResult.Directory)
                        {
                            if (pathState == PathProbeResult.Uncertain)
                            {
                                throw new IOException(
                                    "The browser profile could not be inspected.");
                            }
                            return false;
                        }
                        return _deleteDirectoryTree(path, cancellationToken);
                    },
                    cancellationToken);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maximumAttempts - 1)
                    return false;
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            }
        }
        return false;
    }

    private static void AddImportedSoundReference(
        ISet<string> retained,
        string? fileName)
    {
        if (UiSoundService.IsValidImportedFileName(fileName))
            retained.Add(fileName!);
    }

    private static bool TryGetCanonicalSessionFolder(
        string? accountKey,
        out string sessionFolder)
    {
        if (accountKey?.Equals(
                "legacy",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            sessionFolder = "WebSession";
            return true;
        }

        if (accountKey is { Length: 32 } && accountKey.All(Uri.IsHexDigit))
        {
            sessionFolder = $@"Profiles\{accountKey}";
            return true;
        }

        sessionFolder = string.Empty;
        return false;
    }

    private string GetSessionDataDirectoryForDeletion(AccountProfile profile)
    {
        var path = Path.GetFullPath(
            Path.Combine(_rootDirectory, profile.SessionFolder));
        if (!path.StartsWith(
                _rootDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The account session path is outside SessionDock.");
        }

        EnsureExistingDirectoryPathHasNoReparsePoints(path);
        return path;
    }

    private bool TryLoadFile(
        string path,
        out AppSettings settings,
        out bool migrated,
        out bool accountMetadataWasDiscarded)
    {
        settings = new();
        migrated = false;
        accountMetadataWasDiscarded = false;
        try
        {
            if (ProbePath(path) == PathProbeResult.Missing)
                return false;
            var json = ReadBoundedFile(path, MaximumSettingsBytes);
            using var document = JsonDocument.Parse(json);
            var hasAccountCollection =
                document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(
                    nameof(AppSettings.Accounts),
                    out var accountsElement) &&
                accountsElement.ValueKind == JsonValueKind.Array;
            settings = JsonSerializer.Deserialize<AppSettings>(
                json) ?? new();
            var hadMigratableLegacyAccount =
                settings.LockedUserId is > 0 &&
                !string.IsNullOrWhiteSpace(settings.LockedUsername);
            migrated = MigrateLegacyAccount(settings);
            accountMetadataWasDiscarded = Normalize(
                    settings,
                    out var pendingDeletionStateWasNormalized) ||
                !hasAccountCollection && !hadMigratableLegacyAccount;
            if (accountMetadataWasDiscarded &&
                settings.PendingProfileDeletionKeys.Count > 0)
            {
                settings.PendingProfileDeletionKeys.Clear();
                pendingDeletionStateWasNormalized = true;
            }
            migrated |= pendingDeletionStateWasNormalized;
            migrated |= MigrateLegacyDestinations(settings);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException or ArgumentException or InvalidDataException)
        {
            settings = new();
            migrated = false;
            accountMetadataWasDiscarded = false;
            return false;
        }
    }

    private bool BackupMetadataAllowsProfileCleanup(
        AppSettings primarySettings,
        PathProbeResult backupState)
    {
        if (backupState == PathProbeResult.Missing)
            return true;

        if (backupState != PathProbeResult.File ||
            !TryLoadFile(
                _backupPath,
                out var backupSettings,
                out _,
                out var backupAccountMetadataWasDiscarded) ||
            backupAccountMetadataWasDiscarded)
        {
            return false;
        }

        try
        {
            // The primary remains authoritative for live settings. A profile
            // referenced only by the prior revision is still recoverable,
            // though, unless the crash-durable removal journal names it.
            // Pause broad cleanup instead of silently restoring the account.
            var primaryKeys = primarySettings.Accounts
                .Select(account => account.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var journaledDeletionKeys = GetJournaledProfileDeletionKeys()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return backupSettings.Accounts.All(account =>
                primaryKeys.Contains(account.Key) ||
                journaledDeletionKeys.Contains(account.Key));
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            return false;
        }
    }

    private void WriteFresh(AppSettings settings)
    {
        var temporaryPath = CreateTemporaryPath("restore");
        try
        {
            WriteSettingsFile(temporaryPath, settings);
            _ = ValidateFilePathAndCheckExists(_settingsPath);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFileBestEffort(temporaryPath);
        }
    }

    private static void DeleteTemporaryFileBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            System.Diagnostics.Trace.WriteLine(
                $"Temporary settings cleanup failed: {exception.GetType().Name}.");
        }
    }

    private bool PreserveUnreadableFile(string path)
    {
        var pathState = ProbePath(path);
        if (pathState == PathProbeResult.Missing)
            return true;
        if (pathState != PathProbeResult.File)
            return false;
        try
        {
            var preservedPath = Path.Combine(
                Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
            File.Move(path, preservedPath);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // Never destroy an unreadable settings file just to make recovery tidy.
            return false;
        }
    }

    private bool HasPreservedSettingsFiles()
    {
        try
        {
            return Directory.EnumerateFiles(
                    _rootDirectory,
                    "settings.corrupt-*.json",
                    SearchOption.TopDirectoryOnly)
                .Any() ||
                Directory.EnumerateFiles(
                    _rootDirectory,
                    "settings.backup.corrupt-*.json",
                    SearchOption.TopDirectoryOnly)
                .Any();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            // If the recovery state cannot be inspected, preserve profiles.
            return true;
        }
    }

    private bool HasPotentialAccountProfiles()
    {
        var profilesDirectory = Path.Combine(_rootDirectory, "Profiles");
        var pathState = ProbePath(profilesDirectory);
        if (pathState == PathProbeResult.Missing)
            return false;
        if (pathState != PathProbeResult.Directory)
            return true;

        try
        {
            return Directory.EnumerateDirectories(
                    profilesDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Any(path => IsValidKey(Path.GetFileName(path)));
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            return true;
        }
    }

    private bool RequireProfileCleanupPause()
    {
        var durable = PauseProfileCleanup();
        _profileCleanupGuardRequiredButUnavailable = !durable;
        return durable;
    }

    private bool PauseProfileCleanup()
    {
        CanReconcileProfiles = false;
        if (HasKnownProfileCleanupBlocker())
            return true;

        try
        {
            using var stream = new FileStream(
                _profileCleanupGuardPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(
                "Automatic browser-profile cleanup is paused because settings could not be recovered. Keep this file until account metadata has been recovered or all old profiles may be deleted.");
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            return HasKnownProfileCleanupBlocker();
        }
    }

    private void RefreshProfileCleanupState()
    {
        var blockerState = InspectProfileCleanupBlockers();
        CanReconcileProfiles = !blockerState.HasPossibleBlocker;
        _profileCleanupGuardRequiredButUnavailable =
            blockerState.HasUncertainBlocker && !blockerState.HasKnownBlocker;
    }

    private bool HasKnownProfileCleanupBlocker() =>
        InspectProfileCleanupBlockers().HasKnownBlocker;

    private (bool HasPossibleBlocker, bool HasKnownBlocker, bool HasUncertainBlocker)
        InspectProfileCleanupBlockers()
    {
        var guardState = ProbePath(_profileCleanupGuardPath);
        var conflictState = ProbePath(Path.Combine(
            _rootDirectory,
            AppDataPaths.MigrationConflictFileName));
        var migrationState = ProbePath(Path.Combine(
            _rootDirectory,
            AppDataPaths.MigrationInProgressFileName));
        var hasKnownBlocker =
            guardState is PathProbeResult.File or PathProbeResult.Directory ||
            conflictState is PathProbeResult.File or PathProbeResult.Directory ||
            migrationState is PathProbeResult.File or PathProbeResult.Directory ||
            AppDataPaths.HasMigrationConflict(_rootDirectory) ||
            AppDataPaths.HasIncompleteMigration(_rootDirectory);
        var hasUncertainBlocker =
            guardState == PathProbeResult.Uncertain ||
            conflictState == PathProbeResult.Uncertain ||
            migrationState == PathProbeResult.Uncertain;
        return (
            hasKnownBlocker || hasUncertainBlocker,
            hasKnownBlocker,
            hasUncertainBlocker);
    }

    private void AddProfileCleanupNotice()
    {
        if (!CanReconcileProfiles)
        {
            var cleanupNotice = ProbePath(Path.Combine(
                                     _rootDirectory,
                                     AppDataPaths.MigrationConflictFileName)) !=
                                 PathProbeResult.Missing ||
                                 AppDataPaths.HasMigrationConflict(_rootDirectory)
                ? "SessionDock found separate or conflicting current and legacy RobloxOne data. Conflicting files were left untouched, and automatic browser-profile cleanup is paused. Resolve the preserved legacy data before deleting migration-conflict.txt."
                : ProbePath(Path.Combine(
                        _rootDirectory,
                        AppDataPaths.MigrationInProgressFileName)) !=
                    PathProbeResult.Missing ||
                    AppDataPaths.HasIncompleteMigration(_rootDirectory)
                    ? "A legacy RobloxOne data migration did not finish cleanly. Some files may exist in either data directory, so automatic browser-profile cleanup is paused. Reconcile both directories before deleting migration-in-progress.txt."
                    : ProbePath(Path.Combine(
                            _rootDirectory,
                            AppDataPaths.LegacyInstallMigrationReceiptFileName)) !=
                        PathProbeResult.Missing
                        ? "Automatic browser-profile cleanup remains paused while recovered sessions are being validated. Your account records are available; the pause prevents unreferenced browser profiles from being deleted."
                        : "Automatic browser-profile cleanup is paused to protect sessions whose account metadata could not be recovered.";
            AppendLoadNotice(cleanupNotice);
        }

        if (ProbePath(Path.Combine(
                _rootDirectory,
                AppDataPaths.LegacyOptionalDataNoticeFileName)) !=
            PathProbeResult.Missing)
        {
            AppendLoadNotice(
                "Your accounts and browser profiles were recovered, but a conflicting optional sound or local integration file remains only in the preserved RobloxOne folder. Keep that folder until any optional configuration you still need has been reviewed.");
        }

        ApplyRecoveryNoticeAcknowledgement();
    }

    private void ApplyRecoveryNoticeAcknowledgement()
    {
        if (string.IsNullOrWhiteSpace(LoadNotice))
        {
            _loadNoticeFingerprint = null;
            DeleteTemporaryFileBestEffort(_recoveryNoticeAcknowledgementPath);
            return;
        }

        try
        {
            var currentFingerprint = GetNoticeFingerprint(LoadNotice);
            if (ProbePath(_recoveryNoticeAcknowledgementPath) != PathProbeResult.File)
            {
                _loadNoticeFingerprint = currentFingerprint;
                return;
            }
            ThrowIfReparsePoint(_recoveryNoticeAcknowledgementPath);
            var info = new FileInfo(_recoveryNoticeAcknowledgementPath);
            if (info.Length <= 0 || info.Length > SHA256.HashSizeInBytes * 2 + 2)
            {
                _loadNoticeFingerprint = currentFingerprint;
                return;
            }
            var acknowledged = File.ReadAllText(
                    _recoveryNoticeAcknowledgementPath,
                    Encoding.UTF8)
                .Trim();
            if (acknowledged.Equals(
                    currentFingerprint,
                    StringComparison.Ordinal))
            {
                LoadNotice = null;
                _loadNoticeFingerprint = null;
            }
            else
            {
                _loadNoticeFingerprint = currentFingerprint;
            }
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            // An unreadable acknowledgement never suppresses a safety notice.
            System.Diagnostics.Trace.WriteLine(
                $"Recovery notice acknowledgement could not be read: {exception.GetType().Name}.");
        }
    }

    private string GetNoticeFingerprint(string notice)
    {
        var state = new StringBuilder(notice.Length + 512);
        state.AppendLine(notice);
        foreach (var fileName in new[]
                 {
                     "profile-cleanup-paused.txt",
                     AppDataPaths.MigrationConflictFileName,
                     AppDataPaths.MigrationInProgressFileName,
                     AppDataPaths.LegacyInstallMigrationReceiptFileName,
                     AppDataPaths.LegacyOptionalDataNoticeFileName
                 })
        {
            var path = Path.Combine(_rootDirectory, fileName);
            var pathState = ProbePath(path);
            state.Append(fileName).Append('=').Append(pathState);
            if (pathState == PathProbeResult.File)
            {
                ThrowIfReparsePoint(path);
                var info = new FileInfo(path);
                state.Append('|')
                    .Append(info.CreationTimeUtc.Ticks)
                    .Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks)
                    .Append('|')
                    .Append(info.Length);
                if (info.Length is > 0 and <= MaximumRecoveryNoticeStateBytes)
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                    state.Append('|')
                        .Append(Convert.ToHexString(SHA256.HashData(stream)));
                }
            }
            else if (pathState == PathProbeResult.Directory)
            {
                ThrowIfReparsePoint(path);
                var info = new DirectoryInfo(path);
                state.Append('|')
                    .Append(info.CreationTimeUtc.Ticks)
                    .Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks);
            }
            state.AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(state.ToString())));
    }

    private void AppendLoadNotice(string notice) =>
        LoadNotice = string.IsNullOrWhiteSpace(LoadNotice)
            ? notice
            : $"{LoadNotice}{Environment.NewLine}{Environment.NewLine}{notice}";

    private static bool Normalize(
        AppSettings settings,
        out bool pendingDeletionStateWasNormalized)
    {
        settings.Accounts ??= [];
        settings.RecentExperiences ??= [];
        settings.BatchLaunchPresets ??= [];
        var originalAccountGroups = settings.Accounts
            .Select(account => account?.Group)
            .ToArray();
        var originalBatchLaunchPresets = settings.BatchLaunchPresets;
        var originalBatchLaunchDelaySeconds =
            settings.BatchLaunchDelaySeconds;
        var originalMainWindowPlacement = settings.MainWindowPlacement;
        var originalPendingDeletionKeys =
            settings.PendingProfileDeletionKeys ?? [];
        settings.PendingProfileDeletionKeys = originalPendingDeletionKeys
            .Where(key => TryGetCanonicalSessionFolder(key, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumPendingProfileDeletions)
            .ToList();
        var originalAccountCount = settings.Accounts.Count;
        var validAccounts = settings.Accounts
            .Where(IsValidAccount)
            .Select(NormalizeAccountMetadata)
            .ToList();
        settings.Accounts = validAccounts
            .GroupBy(account => account.UserId)
            .Select(group => group.First())
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        settings.BatchLaunchDelaySeconds =
            BatchLaunchPreferences.NormalizeDelaySeconds(
                settings.BatchLaunchDelaySeconds);
        settings.MainWindowPlacement = WindowPlacementPolicy.Normalize(
            settings.MainWindowPlacement);
        settings.BatchLaunchPresets = BatchLaunchPreferences.NormalizePresets(
                settings.BatchLaunchPresets,
                settings.Accounts)
            .ToList();
        pendingDeletionStateWasNormalized =
            !originalPendingDeletionKeys.SequenceEqual(
                settings.PendingProfileDeletionKeys,
                StringComparer.OrdinalIgnoreCase) ||
            originalBatchLaunchDelaySeconds !=
                settings.BatchLaunchDelaySeconds ||
            !BatchLaunchPreferences.AreEquivalent(
                originalBatchLaunchPresets,
                settings.BatchLaunchPresets) ||
            !WindowPlacementPolicy.AreEquivalent(
                originalMainWindowPlacement,
                settings.MainWindowPlacement) ||
            !originalAccountGroups.SequenceEqual(
                settings.Accounts.Select(account => account.Group),
                StringComparer.Ordinal);

        if (settings.ActiveAccountKey is not null &&
            !settings.Accounts.Any(account =>
                account.Key.Equals(
                    settings.ActiveAccountKey,
                    StringComparison.OrdinalIgnoreCase)))
        {
            settings.ActiveAccountKey = settings.Accounts.FirstOrDefault()?.Key;
        }

        if (!UiSoundService.IsValidStartupSound(settings.StartupSound))
            settings.StartupSound = UiSoundService.DefaultStartupSound;
        if (!UiSoundService.IsValidImportedFileName(
                settings.CustomStartupSoundFileName))
        {
            settings.CustomStartupSoundFileName = null;
        }
        if (settings.StartupSound.Equals(
                UiSoundService.StartupCustom,
                StringComparison.OrdinalIgnoreCase) &&
            settings.CustomStartupSoundFileName is null)
        {
            settings.StartupSound = UiSoundService.DefaultStartupSound;
        }

        var normalizedRecent = settings.RecentExperiences
            .Where(IsValidRecentExperience)
            .Select(NormalizeRecentMetadata)
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.LastLaunchedAt)
            .GroupBy(
                item => $"{item.AccountUserId}:{RecentDestinationIdentity.CreateKey(item)}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        HarmonizeRecentCustomNames(normalizedRecent);
        settings.RecentExperiences = normalizedRecent
            .Where(item => item.IsPinned)
            .Take(50)
            .Concat(normalizedRecent.Where(item => !item.IsPinned).Take(50))
            .ToList();

        return validAccounts.Count != originalAccountCount ||
            settings.Accounts.Count != validAccounts.Count;
    }

    private static bool MigrateLegacyAccount(AppSettings settings)
    {
        settings.Accounts ??= [];
        var changed = false;
        if (settings.Accounts.Count == 0 &&
            settings.LockedUserId is > 0 &&
            !string.IsNullOrWhiteSpace(settings.LockedUsername))
        {
            var migrated = new AccountProfile
            {
                Key = "legacy",
                UserId = settings.LockedUserId.Value,
                Username = settings.LockedUsername,
                SessionFolder = "WebSession"
            };
            settings.Accounts.Add(migrated);
            settings.ActiveAccountKey = migrated.Key;
            changed = true;
        }

        if (settings.LockedUserId is not null || settings.LockedUsername is not null)
        {
            settings.LockedUserId = null;
            settings.LockedUsername = null;
            changed = true;
        }

        return changed;
    }

    private static bool MigrateLegacyDestinations(AppSettings settings)
    {
        var legacyDestination = NormalizeDestination(settings.Destination)
            ?? (settings.PlaceId is > 0 ? settings.PlaceId.Value.ToString() : null);
        var changed = false;

        foreach (var account in settings.Accounts.Where(account =>
                     account.Destination is null && legacyDestination is not null))
        {
            account.Destination = legacyDestination;
            changed = true;
        }

        if (settings.Destination is not null || settings.PlaceId is not null)
        {
            settings.Destination = null;
            settings.PlaceId = null;
            changed = true;
        }

        return changed;
    }

    private static bool IsValidAccount(AccountProfile account)
    {
        if (account is null ||
            account.UserId <= 0 ||
            string.IsNullOrWhiteSpace(account.Username) ||
            account.Username.Length > 50 ||
            !IsValidKey(account.Key))
        {
            return false;
        }

        var expectedFolder = account.Key == "legacy"
            ? "WebSession"
            : $@"Profiles\{account.Key}";
        return account.SessionFolder?.Equals(
            expectedFolder,
            StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsValidRecentExperience(RecentExperience item) =>
        item is not null &&
        item.PlaceId > 0 &&
        !string.IsNullOrWhiteSpace(item.Destination) &&
        item.Destination.Length <= 4096 &&
        (item.Name is null || item.Name.Length <= 200) &&
        (item.CustomName is null || item.CustomName.Length <= 80) &&
        item.AccountUserId >= 0 &&
        (item.AccountUsername is null || item.AccountUsername.Length <= 50) &&
        item.LastLaunchedAt > DateTimeOffset.UnixEpoch &&
        DestinationParser.TryParse(item.Destination, out _, out _);

    private static AccountProfile NormalizeAccountMetadata(AccountProfile account)
    {
        var label = account.Label?.Trim();
        account.Label = string.IsNullOrWhiteSpace(label)
            ? null
            : label[..Math.Min(label.Length, 40)];
        account.Group = BatchLaunchPreferences.NormalizeAccountGroup(
            account.Group);
        account.ColorHex = AccountColors.Contains(
            account.ColorHex ?? string.Empty,
            StringComparer.OrdinalIgnoreCase)
            ? account.ColorHex!.ToUpperInvariant()
            : null;
        account.Destination = NormalizeDestination(account.Destination);
        return account;
    }

    private static RecentExperience NormalizeRecentMetadata(RecentExperience item)
    {
        var customName = item.CustomName?.Trim();
        item.CustomName = string.IsNullOrWhiteSpace(customName)
            ? null
            : customName[..Math.Min(customName.Length, 80)];
        item.ServerJobId = Guid.TryParse(item.ServerJobId, out var serverJobId)
            ? serverJobId.ToString("D")
            : null;
        return item;
    }

    private static void HarmonizeRecentCustomNames(
        IReadOnlyCollection<RecentExperience> recentExperiences)
    {
        foreach (var destinationGroup in recentExperiences.GroupBy(
                     RecentDestinationIdentity.CreateKey,
                     StringComparer.Ordinal))
        {
            var customName = destinationGroup
                .Where(item => item.CustomName is not null)
                .OrderByDescending(item => item.LastLaunchedAt)
                .Select(item => item.CustomName)
                .FirstOrDefault();
            foreach (var item in destinationGroup)
                item.CustomName = customName;
        }
    }

    private static string? NormalizeDestination(string? destination)
    {
        var value = destination?.Trim();
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 4096 &&
               (DestinationParser.TryParse(value, out _, out _) ||
                JoinUserDestination.TryParseStored(value, out _, out _))
            ? value
            : null;
    }

    private static bool IsValidKey(string key) =>
        key == "legacy" ||
        key is { Length: 32 } && key.All(Uri.IsHexDigit);

    private string CreateTemporaryPath(string purpose) => Path.Combine(
        _rootDirectory,
        $"settings.{purpose}.{Guid.NewGuid():N}.tmp");

    private static void WriteSettingsFile(string path, AppSettings settings)
    {
        var contents = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        if (contents.Length > MaximumSettingsBytes)
            throw new InvalidDataException("The settings data is too large.");
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        stream.Write(contents, 0, contents.Length);
        stream.Flush(flushToDisk: true);
    }

    private byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        if (!ValidateFilePathAndCheckExists(path))
            throw new FileNotFoundException("The settings file is missing.", path);
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var output = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = input.Read(chunk, 0, chunk.Length);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("The settings file is too large.");
            output.Write(chunk, 0, read);
        }
    }

    private void EnsureDirectoryPathHasNoReparsePoints(string path)
    {
        ThrowIfReparsePoint(_rootDirectory);
        var relative = Path.GetRelativePath(_rootDirectory, path);
        var current = _rootDirectory;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (Directory.Exists(current))
            {
                ThrowIfReparsePoint(current);
                continue;
            }

            Directory.CreateDirectory(current);
            ThrowIfReparsePoint(current);
        }
    }

    private void EnsureExistingDirectoryPathHasNoReparsePoints(string path)
    {
        ThrowIfReparsePoint(_rootDirectory);
        var relative = Path.GetRelativePath(_rootDirectory, path);
        var current = _rootDirectory;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var state = ProbePath(current);
            if (state == PathProbeResult.Missing || state == PathProbeResult.File)
                return;
            if (state == PathProbeResult.Uncertain)
                throw new IOException("The browser profile could not be inspected.");
            ThrowIfReparsePoint(current);
        }
    }

    private static bool TryDeleteDirectoryTree(
        string root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root) || IsReparsePoint(root))
            return false;

        var pending = new Stack<(string Path, bool ChildrenVisited)>();
        pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ChildrenVisited)
            {
                if (IsReparsePoint(item.Path))
                    return false;
                Directory.Delete(item.Path, recursive: false);
                continue;
            }

            if (IsReparsePoint(item.Path))
                return false;
            pending.Push((item.Path, true));
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         item.Path,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        return false;
                    pending.Push((entry, false));
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        return true;
    }

    private bool ValidateFilePathAndCheckExists(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = _getAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        if ((attributes & FileAttributes.Directory) != 0)
            throw new IOException("An app data file path cannot be a directory.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("An app data file cannot be a reparse point.");
        return true;
    }

    private PathProbeResult ProbePath(string path)
    {
        try
        {
            var attributes = _getAttributes(path);
            return (attributes & FileAttributes.Directory) != 0
                ? PathProbeResult.Directory
                : PathProbeResult.File;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathProbeResult.Missing;
        }
        catch (Exception exception) when (
            LocalDataException.IsExpectedPersistenceFailure(exception))
        {
            return PathProbeResult.Uncertain;
        }
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if (IsReparsePoint(path))
            throw new IOException("An app data directory cannot be a reparse point.");
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

internal readonly record struct ImportedSoundRetention(
    bool CanReconcile,
    bool ReferencesAreComplete,
    IReadOnlySet<string> FileNames);
