using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SessionDock.Models;

namespace SessionDock.Services;

internal sealed record SessionTemplateCatalogReadResult(
    SessionTemplateCatalog Catalog,
    bool Exists,
    bool IsValid,
    bool RecoveredFromBackup,
    bool WasNormalized);

internal sealed class SessionTemplateStore
{
    internal const int MaximumCatalogBytes = 2 * 1024 * 1024;
    internal const string CatalogFileName = "catalog.json";
    internal const string BackupFileName = "catalog.backup.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    private readonly string _rootDirectory;
    private readonly string _templatesDirectory;
    private readonly string _catalogPath;
    private readonly string _backupPath;
    private readonly Func<string, FileAttributes> _getAttributes;

    internal SessionTemplateStore(
        string? rootDirectory = null,
        Func<string, FileAttributes>? getAttributes = null)
    {
        _getAttributes = getAttributes ?? File.GetAttributes;
        _rootDirectory = Path.GetFullPath(
            rootDirectory ?? AppDataPaths.RootDirectory);
        _templatesDirectory = Path.Combine(_rootDirectory, "Templates");
        MacrosDirectory = Path.Combine(_rootDirectory, "Macros");
        _catalogPath = Path.Combine(
            _templatesDirectory,
            CatalogFileName);
        _backupPath = Path.Combine(
            _templatesDirectory,
            BackupFileName);
    }

    internal string MacrosDirectory { get; }

    internal SessionTemplateCatalogReadResult Read()
    {
        try
        {
            var rootState = GetPathState(_rootDirectory);
            if (rootState == StorePathState.Missing)
                return Missing();
            if (rootState != StorePathState.SafeDirectory)
                return Invalid(Exists: true);

            var templatesState = GetPathState(_templatesDirectory);
            var macrosState = GetPathState(MacrosDirectory);
            if (templatesState == StorePathState.Missing)
            {
                return macrosState is StorePathState.Missing or
                    StorePathState.SafeDirectory
                    ? Missing()
                    : Invalid(ExistsUnderRoot());
            }
            if (templatesState != StorePathState.SafeDirectory ||
                macrosState is not (
                    StorePathState.Missing or StorePathState.SafeDirectory))
            {
                return Invalid(ExistsUnderRoot());
            }

            var primaryState = GetPathState(_catalogPath);
            var backupState = GetPathState(_backupPath);
            if (primaryState == StorePathState.Missing &&
                backupState == StorePathState.Missing)
            {
                return Missing();
            }

            if (primaryState == StorePathState.SafeFile &&
                TryReadCatalog(
                    _catalogPath,
                    out var primary,
                    out var primaryWasNormalized))
            {
                return new(
                    primary,
                    Exists: true,
                    IsValid: true,
                    RecoveredFromBackup: false,
                    primaryWasNormalized);
            }

            if (backupState == StorePathState.SafeFile &&
                TryReadCatalog(
                    _backupPath,
                    out var backup,
                    out var backupWasNormalized))
            {
                return new(
                    backup,
                    Exists: true,
                    IsValid: true,
                    RecoveredFromBackup: true,
                    backupWasNormalized);
            }

            return Invalid(Exists: true);
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            return Invalid(ExistsUnderRoot());
        }
    }

    internal void Write(
        SessionTemplateCatalog catalog,
        bool repairInvalidCatalog = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var normalized = SessionTemplatePolicy.Normalize(catalog);
        var contents = Serialize(normalized);
        EnsureSafeDirectories();

        var catalogState = GetPathState(_catalogPath);
        var backupState = GetPathState(_backupPath);
        if (catalogState is not (
                StorePathState.Missing or StorePathState.SafeFile) ||
            backupState is not (
                StorePathState.Missing or StorePathState.SafeFile))
        {
            throw new IOException(
                "The session-template catalog path is not a regular local path.");
        }

        var existingCatalogIsValid =
            catalogState == StorePathState.SafeFile &&
            TryReadCatalog(_catalogPath, out _, out _);
        if (catalogState == StorePathState.SafeFile &&
            !existingCatalogIsValid &&
            !repairInvalidCatalog)
        {
            throw new InvalidDataException(
                "The existing session-template catalog must be repaired first.");
        }

        var temporaryPath = Path.Combine(
            _templatesDirectory,
            $".catalog.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}.tmp");
        try
        {
            WriteNewFile(temporaryPath, contents);
            if (existingCatalogIsValid)
            {
                File.Replace(
                    temporaryPath,
                    _catalogPath,
                    _backupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                // A known-good recovery backup is deliberately preserved when
                // an explicitly authorized repair replaces a corrupt primary.
                File.Move(temporaryPath, _catalogPath, overwrite: true);
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
                // The temporary file contains only the same local metadata.
            }
        }
    }

    private void EnsureSafeDirectories()
    {
        EnsureSafeDirectory(_rootDirectory);
        EnsureSafeDirectory(_templatesDirectory);
        EnsureSafeDirectory(MacrosDirectory);
    }

    private void EnsureSafeDirectory(string path)
    {
        var state = GetPathState(path);
        if (state == StorePathState.Missing)
        {
            Directory.CreateDirectory(path);
            state = GetPathState(path);
        }
        if (state != StorePathState.SafeDirectory)
        {
            throw new IOException(
                "The session-template storage path is not a regular local directory.");
        }
    }

    private static byte[] Serialize(SessionTemplateCatalog catalog)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            catalog,
            JsonOptions);
        if (serialized.Length + 1 > MaximumCatalogBytes)
        {
            throw new InvalidDataException(
                "The session-template catalog exceeds its size boundary.");
        }

        var contents = GC.AllocateUninitializedArray<byte>(
            serialized.Length + 1);
        serialized.CopyTo(contents, 0);
        contents[^1] = (byte)'\n';
        return contents;
    }

    private static void WriteNewFile(string path, byte[] contents)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.WriteThrough);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    private bool TryReadCatalog(
        string path,
        out SessionTemplateCatalog catalog,
        out bool wasNormalized)
    {
        catalog = SessionTemplatePolicy.CreateDefault();
        wasNormalized = false;
        try
        {
            if (GetPathState(path) != StorePathState.SafeFile)
                return false;
            var bytes = ReadBoundedFile(path);
            _ = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            if (!HasUniqueProperties(document.RootElement) ||
                !HasExpectedCatalogShape(
                    document.RootElement,
                    out var catalogSchemaVersion))
                return false;

            var parsed = JsonSerializer.Deserialize<SessionTemplateCatalog>(
                bytes,
                JsonOptions);
            if (parsed is null)
                return false;
            if (catalogSchemaVersion ==
                SessionTemplatePolicy.LegacyCatalogSchemaVersion)
            {
                InferLegacyMacroKinds(document.RootElement, parsed);
            }
            if (!SessionTemplatePolicy.TryNormalize(
                    parsed,
                    out var normalized))
            {
                return false;
            }

            wasNormalized = !SessionTemplatePolicy.AreEquivalent(
                parsed,
                normalized);
            catalog = normalized;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or DecoderFallbackException or
                InvalidDataException or ArgumentException or
                NotSupportedException)
        {
            return false;
        }
    }

    private static byte[] ReadBoundedFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumCatalogBytes)
        {
            throw new InvalidDataException(
                "The session-template catalog is outside its size boundary.");
        }

        using var buffer = new MemoryStream(
            (int)Math.Min(stream.Length, MaximumCatalogBytes));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > MaximumCatalogBytes)
            {
                throw new InvalidDataException(
                    "The session-template catalog grew beyond its size boundary.");
            }
            buffer.Write(chunk, 0, read);
        }
    }

    private static bool HasUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) ||
                    !HasUniqueProperties(property.Value))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (!HasUniqueProperties(item))
                    return false;
            }
        }

        return true;
    }

    private static bool HasExpectedCatalogShape(
        JsonElement root,
        out int catalogSchemaVersion)
    {
        catalogSchemaVersion = 0;
        if (!HasExactProperties(
                root,
                "schemaVersion",
                "templates",
                "macroDefinitions",
                "templatePreferences") ||
            root.GetProperty("schemaVersion").ValueKind !=
                JsonValueKind.Number ||
            !root.GetProperty("schemaVersion").TryGetInt32(
                out catalogSchemaVersion) ||
            catalogSchemaVersion is not (
                SessionTemplatePolicy.LegacyCatalogSchemaVersion or
                SessionTemplatePolicy.PreviousCatalogSchemaVersion or
                SessionTemplatePolicy.CatalogSchemaVersion) ||
            root.GetProperty("templates").ValueKind != JsonValueKind.Array ||
            root.GetProperty("macroDefinitions").ValueKind !=
                JsonValueKind.Array)
        {
            return false;
        }

        var preferences = root.GetProperty("templatePreferences");
        var hasExpectedPreferences = catalogSchemaVersion switch
        {
            SessionTemplatePolicy.LegacyCatalogSchemaVersion =>
                HasExactProperties(
                preferences,
                "autoArrangeNormalBatch",
                "targetWidth",
                "targetHeight",
                "minimumWidth",
                "minimumHeight",
                "revealX",
                "revealY",
                "preferredMonitorDeviceName"),
            SessionTemplatePolicy.PreviousCatalogSchemaVersion =>
                HasExactProperties(
                preferences,
                "autoArrangeNormalBatch",
                "targetWidth",
                "targetHeight",
                "minimumWidth",
                "minimumHeight",
                "revealX",
                "revealY",
                "preferredMonitorDeviceName",
                "macroPlaybackSpeed"),
            SessionTemplatePolicy.CatalogSchemaVersion =>
                HasExactProperties(
                preferences,
                "autoArrangeNormalBatch",
                "targetWidth",
                "targetHeight",
                "minimumWidth",
                "minimumHeight",
                "revealX",
                "revealY",
                "preferredMonitorDeviceName",
                "macroPlaybackSpeed",
                "macroRecordingStopHotkey"),
            _ => false
        };
        if (!hasExpectedPreferences)
            return false;

        foreach (var template in root.GetProperty("templates").EnumerateArray())
        {
            if (!HasExpectedTemplateProperties(template) ||
                template.GetProperty("clientSlots").ValueKind !=
                    JsonValueKind.Array)
            {
                return false;
            }

            foreach (var slot in template.GetProperty("clientSlots")
                         .EnumerateArray())
            {
                if (!HasExactProperties(
                        slot,
                        "slotId",
                        "accountKey",
                        "order",
                        "destination",
                        "placement",
                        "perClientMacroId"))
                {
                    return false;
                }

                var placement = slot.GetProperty("placement");
                if (placement.ValueKind != JsonValueKind.Null &&
                    !HasExpectedPlacementProperties(placement))
                {
                    return false;
                }
            }
        }

        foreach (var macro in root.GetProperty("macroDefinitions")
                     .EnumerateArray())
        {
            var hasExpectedMacro = catalogSchemaVersion ==
                SessionTemplatePolicy.LegacyCatalogSchemaVersion
                ? HasExpectedLegacyMacroProperties(macro)
                : HasExactProperties(
                    macro,
                    "contentId",
                    "safeFileName",
                    "name",
                    "kind",
                    "recordedAccountKey",
                    "durationMilliseconds",
                    "eventCount",
                    "sha256",
                    "recordedAtUtc");
            if (!hasExpectedMacro)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExpectedPlacementProperties(JsonElement placement)
    {
        string[] required =
        [
            "monitorDeviceName",
            "monitorIndex",
            "left",
            "top",
            "width",
            "height"
        ];
        const string optionalStableId = "monitorStableId";
        if (placement.ValueKind != JsonValueKind.Object ||
            placement.GetPropertyCount() is not (6 or 7))
        {
            return false;
        }

        var names = placement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!required.All(names.Contains) ||
            !names.All(name =>
                required.Contains(name, StringComparer.Ordinal) ||
                name.Equals(optionalStableId, StringComparison.Ordinal)))
        {
            return false;
        }

        if (!placement.TryGetProperty(optionalStableId, out var stableId))
            return true;
        if (stableId.ValueKind != JsonValueKind.String)
            return false;
        var value = stableId.GetString();
        return !string.IsNullOrWhiteSpace(value) &&
            value.Length <= SessionTemplatePolicy.MaximumMonitorStableIdLength &&
            !value.Any(char.IsControl);
    }

    private static bool HasExpectedLegacyMacroProperties(JsonElement macro)
    {
        string[] required =
        [
            "contentId",
            "safeFileName",
            "name",
            "recordedAccountKey",
            "durationMilliseconds",
            "eventCount",
            "sha256",
            "recordedAtUtc"
        ];
        const string optionalKind = "kind";
        if (macro.ValueKind != JsonValueKind.Object ||
            macro.GetPropertyCount() is not (8 or 9))
        {
            return false;
        }

        var names = macro.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        return required.All(names.Contains) &&
            names.All(name =>
                required.Contains(name, StringComparer.Ordinal) ||
                name.Equals(optionalKind, StringComparison.Ordinal));
    }

    private static void InferLegacyMacroKinds(
        JsonElement root,
        SessionTemplateCatalog catalog)
    {
        var macroElements = root.GetProperty("macroDefinitions")
            .EnumerateArray()
            .ToArray();
        var definitions = catalog.MacroDefinitions ?? [];
        if (macroElements.Length != definitions.Count)
        {
            throw new InvalidDataException(
                "The legacy macro catalog could not be migrated safely.");
        }

        for (var index = 0; index < macroElements.Length; index++)
        {
            if (macroElements[index].TryGetProperty("kind", out _))
                continue;
            definitions[index].Kind = InferLegacyMacroKind(
                definitions[index],
                catalog.Templates ?? []);
        }
    }

    private static SessionMacroKind InferLegacyMacroKind(
        MacroDefinition definition,
        IReadOnlyList<SessionTemplate> templates)
    {
        if (HasKindSpecificContentId(
                definition,
                SessionMacroKind.WholeLayout))
        {
            return SessionMacroKind.WholeLayout;
        }
        if (HasKindSpecificContentId(
                definition,
                SessionMacroKind.Client))
        {
            return SessionMacroKind.Client;
        }

        var referencedByClient = false;
        var referencedByWholeLayout = false;
        foreach (var template in templates)
        {
            if (template is null)
                continue;
            switch (template.MacroMode)
            {
                case SessionTemplateMacroMode.PerClient:
                    referencedByClient |= (template.ClientSlots ?? [])
                        .Any(slot => slot is not null && string.Equals(
                            slot.PerClientMacroId,
                            definition.ContentId,
                            StringComparison.OrdinalIgnoreCase));
                    break;
                case SessionTemplateMacroMode.Shared:
                    referencedByClient |= string.Equals(
                        template.SharedMacroId,
                        definition.ContentId,
                        StringComparison.OrdinalIgnoreCase);
                    break;
                case SessionTemplateMacroMode.WholeLayout:
                    referencedByWholeLayout |= string.Equals(
                        template.WholeLayoutMacroId,
                        definition.ContentId,
                        StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        // A legacy byte-only id may safely become whole-layout only when its
        // template usage is unambiguous. Conflicting or unused definitions
        // retain the historical client default and will fail kind checks if
        // assigned to a whole-layout template.
        return referencedByWholeLayout && !referencedByClient
            ? SessionMacroKind.WholeLayout
            : SessionMacroKind.Client;
    }

    private static bool HasKindSpecificContentId(
        MacroDefinition definition,
        SessionMacroKind kind)
    {
        if (definition.Sha256 is not { Length: 64 } sha256 ||
            !sha256.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        var prefix = kind switch
        {
            SessionMacroKind.Client => "ew-client-",
            SessionMacroKind.WholeLayout => "ew-whole-layout-",
            _ => string.Empty
        };
        return prefix.Length > 0 && string.Equals(
            definition.ContentId,
            prefix + sha256.ToLowerInvariant(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExpectedTemplateProperties(JsonElement template)
    {
        string[] required =
        [
            "schemaVersion",
            "id",
            "name",
            "delaySeconds",
            "layoutMode",
            "macroMode",
            "clientSlots",
            "sharedMacroId",
            "wholeLayoutMacroId",
            "repeatWholeLayoutMacro",
            "updatedAtUtc",
            "legacyPresetName"
        ];
        const string optionalTargets = "sharedMacroAccountKeys";
        if (template.ValueKind != JsonValueKind.Object ||
            template.GetPropertyCount() is not (12 or 13))
        {
            return false;
        }

        var names = template.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!required.All(names.Contains) ||
            names.Any(name =>
                !required.Contains(name, StringComparer.Ordinal) &&
                !name.Equals(optionalTargets, StringComparison.Ordinal)))
        {
            return false;
        }

        if (!template.TryGetProperty(optionalTargets, out var targets))
            return true;
        if (targets.ValueKind == JsonValueKind.Null)
            return true;
        return targets.ValueKind == JsonValueKind.Array &&
            targets.EnumerateArray().All(item =>
                item.ValueKind == JsonValueKind.String);
    }

    private static bool HasExactProperties(
        JsonElement element,
        params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            element.GetPropertyCount() != expected.Length)
        {
            return false;
        }

        var names = element.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        return expected.All(names.Contains);
    }

    private StorePathState GetPathState(string path)
    {
        try
        {
            var attributes = _getAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return StorePathState.Unsafe;
            return (attributes & FileAttributes.Directory) != 0
                ? StorePathState.SafeDirectory
                : StorePathState.SafeFile;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return StorePathState.Missing;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException)
        {
            return StorePathState.Unsafe;
        }
    }

    private bool ExistsUnderRoot() =>
        GetPathState(_templatesDirectory) != StorePathState.Missing ||
        GetPathState(MacrosDirectory) != StorePathState.Missing ||
        GetPathState(_catalogPath) != StorePathState.Missing ||
        GetPathState(_backupPath) != StorePathState.Missing;

    private static SessionTemplateCatalogReadResult Missing() => new(
        SessionTemplatePolicy.CreateDefault(),
        Exists: false,
        IsValid: true,
        RecoveredFromBackup: false,
        WasNormalized: false);

    private static SessionTemplateCatalogReadResult Invalid(bool Exists) =>
        new(
            SessionTemplatePolicy.CreateDefault(),
            Exists,
            IsValid: false,
            RecoveredFromBackup: false,
            WasNormalized: false);

    private static bool IsExpectedFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            JsonException or DecoderFallbackException or
            InvalidDataException or ArgumentException or
            NotSupportedException;

    private enum StorePathState
    {
        Missing,
        SafeFile,
        SafeDirectory,
        Unsafe
    }
}
