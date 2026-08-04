using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SessionDock.ExactWheel;
using SessionDock.Models;

namespace SessionDock.Services;

internal sealed record ExactWheelMacroSaveResult(
    MacroDefinition Definition,
    bool PayloadCreated);

internal enum MacroArtifactCleanupResult
{
    Retained,
    DeletedOrMissing,
    Failed
}

internal sealed class ExactWheelMacroStore
{
    internal const string MacroFileExtension = ".ewmacro";

    private readonly string _rootDirectory;
    private readonly string _macrosDirectory;
    private readonly Func<string, FileAttributes> _getAttributes;
    private readonly Func<DateTimeOffset> _utcNow;

    internal ExactWheelMacroStore(
        SessionTemplateStore templateStore,
        Func<string, FileAttributes>? getAttributes = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(templateStore);
        _macrosDirectory = Path.GetFullPath(templateStore.MacrosDirectory);
        _rootDirectory = Path.GetDirectoryName(_macrosDirectory) ??
            throw new ArgumentException(
                "The macro catalog directory has no parent.",
                nameof(templateStore));
        _getAttributes = getAttributes ?? File.GetAttributes;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal MacroDefinition Save(
        string name,
        SessionMacroKind kind,
        ExactWheelRecording recording,
        string? recordedAccountKey = null) =>
        SaveWithResult(name, kind, recording, recordedAccountKey).Definition;

    internal ExactWheelMacroSaveResult SaveWithResult(
        string name,
        SessionMacroKind kind,
        ExactWheelRecording recording,
        string? recordedAccountKey = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var safeName = NormalizeName(name);
        var accountKey = NormalizeAccountKey(recordedAccountKey, kind);
        var bytes = ExactWheelMacroSerializer.Serialize(recording);
        var hashBytes = SHA256.HashData(bytes);
        var hash = Convert.ToHexString(hashBytes);
        var safeFileName = GetSafeFileName(hash);
        var definition = new MacroDefinition
        {
            ContentId = GetContentId(hash, kind),
            SafeFileName = safeFileName,
            Name = safeName,
            Kind = kind,
            RecordedAccountKey = accountKey,
            DurationMilliseconds = checked((long)(
                (recording.DurationMicroseconds + 999UL) / 1_000UL)),
            EventCount = recording.Events.Count,
            Sha256 = hash,
            RecordedAtUtc = _utcNow().ToUniversalTime()
        };
        EnsureSafeStorage();
        var destinationPath = ResolveCatalogPath(safeFileName);
        var payloadCreated = SaveContentAddressed(
            destinationPath,
            bytes,
            hashBytes);

        return new ExactWheelMacroSaveResult(definition, payloadCreated);
    }

    internal ExactWheelRecording Load(MacroDefinition definition)
    {
        var bytes = ReadExactBytes(definition);
        return ExactWheelMacroSerializer.Deserialize(bytes);
    }

    // Portable packages must preserve the content-addressed recording exactly.
    // Returning the verified stored bytes avoids a deserialize/reserialize pass
    // that could otherwise change the hash or silently rewrite future fields.
    internal byte[] ReadExactBytes(MacroDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var expectedHash = ValidateDefinition(definition);
        EnsureSafeStorage(createIfMissing: false);
        var path = ResolveCatalogPath(definition.SafeFileName);
        var bytes = ReadBoundedRegularFile(path);
        ValidateExactBytes(definition, bytes, expectedHash);
        return bytes;
    }

    // Import is content-addressed and idempotent. The supplied catalog
    // definition and payload are both revalidated before any file is created.
    internal bool SaveExactBytes(
        MacroDefinition definition,
        byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bytes);
        var expectedHash = ValidateDefinition(definition);
        ValidateExactBytes(definition, bytes, expectedHash);
        EnsureSafeStorage();
        var destinationPath = ResolveCatalogPath(definition.SafeFileName);
        return SaveContentAddressed(destinationPath, bytes, expectedHash);
    }

    // Rollback cleanup is restricted to a verified content-addressed payload.
    // Callers must additionally prove that the current catalog does not refer
    // to the definition; this method never follows links or deletes a mismatch.
    internal bool TryDeleteExactBytes(MacroDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var expectedHash = ValidateDefinition(definition);
        try
        {
            EnsureSafeStorage(createIfMissing: false);
            var path = ResolveCatalogPath(definition.SafeFileName);
            if (GetPathState(path) == MacroPathState.Missing)
                return true;
            if (GetPathState(path) != MacroPathState.SafeFile)
                return false;
            VerifyHash(ReadBoundedRegularFile(path), expectedHash);
            if (GetPathState(path) != MacroPathState.SafeFile)
                return false;
            File.Delete(path);
            return GetPathState(path) == MacroPathState.Missing;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or InvalidDataException or
                NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    // Cleanup is deliberately coupled to the catalog that is authoritative
    // after a commit or rollback. A content-addressed file can back multiple
    // definitions (for example client and whole-layout definitions), so a
    // matching SafeFileName anywhere in that catalog always wins over cleanup.
    internal MacroArtifactCleanupResult TryDeleteExactBytesIfUnreferenced(
        MacroDefinition definition,
        IEnumerable<MacroDefinition> resultingDefinitions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(resultingDefinitions);
        if (resultingDefinitions.Any(candidate =>
                string.Equals(
                    candidate.SafeFileName,
                    definition.SafeFileName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return MacroArtifactCleanupResult.Retained;
        }

        try
        {
            return TryDeleteExactBytes(definition)
                ? MacroArtifactCleanupResult.DeletedOrMissing
                : MacroArtifactCleanupResult.Failed;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or InvalidDataException or
                NotSupportedException or ArgumentException)
        {
            return MacroArtifactCleanupResult.Failed;
        }
    }

    internal static IReadOnlyList<MacroDefinition>
        FindNewlyUnreferencedPayloads(
            IEnumerable<MacroDefinition> previousDefinitions,
            IEnumerable<MacroDefinition> resultingDefinitions)
    {
        ArgumentNullException.ThrowIfNull(previousDefinitions);
        ArgumentNullException.ThrowIfNull(resultingDefinitions);
        var retainedFileNames = resultingDefinitions
            .Select(definition => definition.SafeFileName)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return previousDefinitions
            .Where(definition =>
                !string.IsNullOrWhiteSpace(definition.SafeFileName) &&
                !retainedFileNames.Contains(definition.SafeFileName))
            .GroupBy(
                definition => definition.SafeFileName,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    internal MacroDefinition ImportLegacyWholeLayout(
        string sourcePath,
        string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        if (!Path.GetExtension(fullPath).Equals(
                MacroFileExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Only an explicitly selected .ewmacro file can be imported.");
        }

        var bytes = ReadBoundedRegularFile(fullPath);
        var recording = ExactWheelMacroSerializer.Deserialize(bytes);
        return Save(
            name,
            SessionMacroKind.WholeLayout,
            recording,
            recordedAccountKey: null);
    }

    private bool SaveContentAddressed(
        string destinationPath,
        byte[] bytes,
        byte[] expectedHash)
    {
        var state = GetPathState(destinationPath);
        if (state == MacroPathState.SafeFile)
        {
            VerifyHash(ReadBoundedRegularFile(destinationPath), expectedHash);
            return false;
        }
        if (state != MacroPathState.Missing)
        {
            throw new IOException(
                "The macro destination is not a regular local file path.");
        }

        var temporaryPath = Path.Combine(
            _macrosDirectory,
            $".macro.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
                return true;
            }
            catch (IOException) when (
                GetPathState(destinationPath) == MacroPathState.SafeFile)
            {
                VerifyHash(
                    ReadBoundedRegularFile(destinationPath),
                    expectedHash);
                return false;
            }
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
                // The uniquely named temporary contains only inert macro bytes.
            }
        }
    }

    private byte[] ReadBoundedRegularFile(string path)
    {
        if (GetPathState(path) != MacroPathState.SafeFile)
        {
            throw new IOException(
                "The macro path is missing, a directory, or a reparse point.");
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (GetPathState(path) != MacroPathState.SafeFile)
        {
            throw new IOException(
                "The macro path changed while it was being opened.");
        }
        if (stream.Length <
                ExactWheelMacroSerializer.FixedHeaderBytes + sizeof(uint) ||
            stream.Length > ExactWheelLimits.MaximumMacroFileBytes ||
            stream.Length > int.MaxValue)
        {
            throw new InvalidDataException(
                "The macro file is outside the ExactWheel size boundary.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(
            checked((int)stream.Length));
        stream.ReadExactly(bytes);
        return bytes;
    }

    private void EnsureSafeStorage(bool createIfMissing = true)
    {
        EnsureSafeDirectory(_rootDirectory, createIfMissing);
        EnsureSafeDirectory(_macrosDirectory, createIfMissing);
    }

    private void EnsureSafeDirectory(string path, bool createIfMissing)
    {
        var state = GetPathState(path);
        if (state == MacroPathState.Missing && createIfMissing)
        {
            Directory.CreateDirectory(path);
            state = GetPathState(path);
        }
        if (state != MacroPathState.SafeDirectory)
        {
            throw new IOException(
                "The macro catalog storage is not a regular local directory.");
        }
    }

    private MacroPathState GetPathState(string path)
    {
        try
        {
            var attributes = _getAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return MacroPathState.Unsafe;
            return (attributes & FileAttributes.Directory) != 0
                ? MacroPathState.SafeDirectory
                : MacroPathState.SafeFile;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return MacroPathState.Missing;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException)
        {
            return MacroPathState.Unsafe;
        }
    }

    private string ResolveCatalogPath(string safeFileName)
    {
        if (!IsSafeFileName(safeFileName))
            throw new InvalidDataException("The macro file name is unsafe.");
        var fullPath = Path.GetFullPath(
            Path.Combine(_macrosDirectory, safeFileName));
        var relative = Path.GetRelativePath(_macrosDirectory, fullPath);
        if (!relative.Equals(safeFileName, StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(relative) ||
            relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The macro path escapes the catalog Macro directory.");
        }

        return fullPath;
    }

    private static byte[] ValidateDefinition(MacroDefinition definition)
    {
        if (!Enum.IsDefined(definition.Kind) ||
            definition.EventCount is < 0 or >
                SessionTemplatePolicy.MaximumEventCount ||
            definition.DurationMilliseconds is < 0 or >
                SessionTemplatePolicy.MaximumDurationMilliseconds ||
            definition.Sha256 is not { Length: 64 } ||
            !definition.Sha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException(
                "The macro catalog definition is invalid.");
        }

        var normalizedHash = definition.Sha256.ToUpperInvariant();
        if (!string.Equals(
                definition.SafeFileName,
                GetSafeFileName(normalizedHash),
                StringComparison.OrdinalIgnoreCase) ||
            !HasExpectedContentId(
                definition.ContentId,
                normalizedHash,
                definition.Kind))
        {
            throw new InvalidDataException(
                "The macro catalog identity is not content-addressed.");
        }

        return Convert.FromHexString(normalizedHash);
    }

    private static void VerifyHash(byte[] bytes, byte[] expectedHash)
    {
        var actualHash = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                expectedHash))
        {
            throw new InvalidDataException(
                "The macro content hash does not match the catalog.");
        }
    }

    private static void ValidateExactBytes(
        MacroDefinition definition,
        byte[] bytes,
        byte[] expectedHash)
    {
        if (bytes.Length <
                ExactWheelMacroSerializer.FixedHeaderBytes + sizeof(uint) ||
            bytes.Length > ExactWheelLimits.MaximumMacroFileBytes)
        {
            throw new InvalidDataException(
                "The macro payload is outside the ExactWheel size boundary.");
        }

        VerifyHash(bytes, expectedHash);
        var recording = ExactWheelMacroSerializer.Deserialize(bytes);
        ExactWheelRecordingValidator.ValidatePlayable(recording);
        var durationMilliseconds = checked((long)(
            (recording.DurationMicroseconds + 999UL) / 1_000UL));
        if (recording.Events.Count != definition.EventCount ||
            durationMilliseconds != definition.DurationMilliseconds)
        {
            throw new InvalidDataException(
                "The macro catalog metadata does not match the macro contents.");
        }
    }

    private static string GetSafeFileName(string sha256) =>
        sha256.ToLowerInvariant() + MacroFileExtension;

    private static string GetContentId(
        string sha256,
        SessionMacroKind kind) =>
        kind switch
        {
            SessionMacroKind.Client =>
                "ew-client-" + sha256.ToLowerInvariant(),
            SessionMacroKind.WholeLayout =>
                "ew-whole-layout-" + sha256.ToLowerInvariant(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static bool HasExpectedContentId(
        string contentId,
        string sha256,
        SessionMacroKind kind) =>
        string.Equals(
            contentId,
            GetContentId(sha256, kind),
            StringComparison.Ordinal) ||
        // Schema-v1 catalogs used a byte-only identity. Continue accepting
        // it so existing template references load without migration.
        string.Equals(
            contentId,
            "ew-" + sha256.ToLowerInvariant(),
            StringComparison.Ordinal);

    private static bool IsSafeFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= SessionTemplatePolicy.MaximumSafeFileNameLength &&
        value.Equals(Path.GetFileName(value), StringComparison.Ordinal) &&
        value.EndsWith(MacroFileExtension, StringComparison.OrdinalIgnoreCase) &&
        !value.EndsWith(' ') &&
        !value.EndsWith('.') &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var builder = new StringBuilder(
            Math.Min(name.Length, SessionTemplatePolicy.MaximumNameLength));
        var pendingSpace = false;
        foreach (var rune in name.Trim().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) ||
                Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            var additional = rune.Utf16SequenceLength + (pendingSpace ? 1 : 0);
            if (builder.Length + additional >
                SessionTemplatePolicy.MaximumNameLength)
            {
                break;
            }
            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(rune.ToString());
        }

        if (builder.Length == 0)
            throw new ArgumentException("The macro name is empty.", nameof(name));
        return builder.ToString();
    }

    private static string? NormalizeAccountKey(
        string? accountKey,
        SessionMacroKind kind)
    {
        if (kind == SessionMacroKind.WholeLayout)
            return null;
        var normalized = accountKey?.Trim();
        if (string.IsNullOrEmpty(normalized))
            return null;
        if (normalized.Length > SessionTemplatePolicy.MaximumIdentifierLength ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('_' or '-')))
        {
            throw new ArgumentException(
                "The recorded account key is invalid.",
                nameof(accountKey));
        }

        return normalized;
    }

    private enum MacroPathState
    {
        Missing,
        SafeFile,
        SafeDirectory,
        Unsafe
    }
}
