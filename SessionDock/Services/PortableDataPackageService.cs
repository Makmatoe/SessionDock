using System.Buffers;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SessionDock.ExactWheel;
using SessionDock.Models;

namespace SessionDock.Services;

internal static class PortableDataPackageService
{
    internal const string FormatName = "sessiondock.portable";
    internal const int CurrentVersion = 1;
    internal const string ManifestEntryName = "manifest.json";
    internal const int MaximumManifestBytes = 2 * 1024 * 1024;
    internal const int MaximumArchiveBytes = 256 * 1024 * 1024;
    internal const long MaximumExpandedBytes = 256L * 1024L * 1024L;
    internal const int MaximumMacroEntries = 32;
    internal const int MaximumArchiveEntries = MaximumMacroEntries + 1;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly SearchValues<char> LowerHexCharacters =
        SearchValues.Create("0123456789abcdef");
    private static readonly SearchValues<char> DecimalCharacters =
        SearchValues.Create("0123456789");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Default,
        MaxDepth = 20,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    internal static PortableExportPackage PrepareExport(
        AppSettings settings,
        SessionTemplateCatalog catalog,
        PortablePackageSelection selection,
        Func<MacroDefinition, byte[]> readMacroBytes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(readMacroBytes);

        var templateIds = NormalizeSelection(
            selection.TemplateIds,
            SessionTemplatePolicy.MaximumTemplates,
            "template");
        var macroIds = NormalizeSelection(
            selection.MacroContentIds,
            MaximumMacroEntries,
            "macro");
        var destinationIds = NormalizeSelection(
            selection.NamedDestinationIds,
            NamedDestinationPolicy.MaximumDestinations,
            "named destination");
        var presetIds = NormalizeSelection(
            selection.BatchPresetIds,
            BatchLaunchPreferences.MaximumPresets,
            "batch preset");
        if (templateIds.Count == 0 && macroIds.Count == 0 &&
            destinationIds.Count == 0 && presetIds.Count == 0)
        {
            throw new ArgumentException(
                "Select at least one portable item.",
                nameof(selection));
        }

        var normalizedCatalog = SessionTemplatePolicy.Normalize(catalog);
        var accountByKey = BuildAccountByKey(settings.Accounts);
        var selectedTemplates = SelectTemplates(
            normalizedCatalog,
            templateIds);
        var definitionsById = BuildDefinitionsById(normalizedCatalog);
        var selectedDefinitions = new Dictionary<string, MacroDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var macroId in macroIds)
        {
            if (!definitionsById.TryGetValue(macroId, out var definition))
            {
                throw new InvalidDataException(
                    $"The selected macro '{macroId}' is unavailable.");
            }
            selectedDefinitions.Add(definition.ContentId, definition);
        }

        foreach (var template in selectedTemplates)
        {
            var resolution = SessionTemplateMacroAssignmentPolicy.Resolve(
                template,
                normalizedCatalog);
            if (!resolution.IsFullyValid)
            {
                throw new InvalidDataException(
                    $"Template '{template.Name}' has an invalid macro assignment.");
            }
            foreach (var assignment in resolution.ValidAssignments)
            {
                selectedDefinitions.TryAdd(
                    assignment.Definition.ContentId,
                    assignment.Definition);
            }
        }
        if (selectedDefinitions.Count > MaximumMacroEntries)
        {
            throw new InvalidDataException(
                $"A portable package can contain at most {MaximumMacroEntries} macros.");
        }

        var macros = PrepareExportMacros(
            selectedDefinitions.Values,
            accountByKey,
            readMacroBytes);
        var macroIdMap = macros.SourceContentIdMap;
        var normalizedDestinations = NamedDestinationPolicy.Normalize(
            settings.NamedDestinations,
            settings.Accounts);
        var destinationsById = normalizedDestinations.ToDictionary(
            destination => destination.Id,
            StringComparer.OrdinalIgnoreCase);
        var selectedDestinations = new List<NamedDestination>();
        var selectedDestinationIds = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var destinationId in destinationIds)
        {
            if (!destinationsById.TryGetValue(destinationId, out var source))
            {
                throw new InvalidDataException(
                    $"The selected named destination '{destinationId}' is unavailable.");
            }
            selectedDestinationIds.Add(source.Id);
            selectedDestinations.Add(source);
        }

        // A template stores the resolved destination on each account slot so
        // it can launch independently. Also carry a matching named destination
        // and its account assignments when one exists. This keeps the richer
        // destination library intact after a portable round trip without
        // requiring users to discover and select the dependency separately.
        foreach (var source in normalizedDestinations)
        {
            if (selectedDestinationIds.Contains(source.Id) ||
                !IsTemplateDestinationDependency(source, selectedTemplates))
            {
                continue;
            }
            selectedDestinationIds.Add(source.Id);
            selectedDestinations.Add(source);
        }

        var namedDestinations = new List<PortableManifestNamedDestination>();
        var omittedNamedDestinations = 0;
        foreach (var source in selectedDestinations)
        {
            if (!TryProjectPublicPlace(source.Value, out var placeId))
            {
                omittedNamedDestinations++;
                continue;
            }

            namedDestinations.Add(new PortableManifestNamedDestination
            {
                PortableId = $"destination-{namedDestinations.Count + 1}",
                Name = NormalizePortableName(
                    source.Name,
                    NamedDestinationPolicy.MaximumNameLength),
                PlaceId = placeId,
                AccountUserIds = source.AccountKeys
                    .Where(accountByKey.ContainsKey)
                    .Select(key => accountByKey[key].UserId)
                    .Where(userId => userId > 0)
                    .Distinct()
                    .ToList()
            });
        }

        var presets = new List<PortableManifestBatchPreset>();
        var normalizedPresets = BatchLaunchPreferences.NormalizePresets(
            settings.BatchLaunchPresets,
            settings.Accounts);
        foreach (var presetId in presetIds)
        {
            var source = normalizedPresets.SingleOrDefault(preset =>
                preset.Name.Equals(
                    presetId,
                    StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidDataException(
                    $"The selected batch preset '{presetId}' is unavailable.");
            var userIds = source.AccountKeys
                .Where(accountByKey.ContainsKey)
                .Select(key => accountByKey[key].UserId)
                .Where(userId => userId > 0)
                .Distinct()
                .ToList();
            if (userIds.Count < 2)
            {
                throw new InvalidDataException(
                    $"Batch preset '{source.Name}' has fewer than two portable accounts.");
            }
            presets.Add(new PortableManifestBatchPreset
            {
                PortableId = $"preset-{presets.Count + 1}",
                Name = source.Name,
                DelaySeconds = source.DelaySeconds,
                AccountUserIds = userIds
            });
        }

        var projectedTemplates = new List<PortableManifestTemplate>();
        var omittedTemplateDestinations = 0;
        foreach (var source in selectedTemplates)
        {
            var projected = ProjectTemplate(
                source,
                projectedTemplates.Count + 1,
                accountByKey,
                macroIdMap,
                ref omittedTemplateDestinations);
            projectedTemplates.Add(projected);
        }

        var preferences = normalizedCatalog.TemplatePreferences;
        var manifest = new PortableManifest
        {
            Format = FormatName,
            Version = CurrentVersion,
            LayoutProfile = new PortableManifestLayoutProfile
            {
                TargetWidth = preferences.TargetWidth,
                TargetHeight = preferences.TargetHeight,
                MinimumWidth = preferences.MinimumWidth,
                MinimumHeight = preferences.MinimumHeight,
                RevealX = preferences.RevealX,
                RevealY = preferences.RevealY
            },
            Omissions = new PortableManifestOmissions
            {
                NamedDestinations = omittedNamedDestinations,
                TemplateSlotDestinations = omittedTemplateDestinations
            },
            Macros = macros.ManifestMacros,
            NamedDestinations = namedDestinations,
            BatchPresets = presets,
            Templates = projectedTemplates
        };
        var manifestBytes = SerializeManifest(manifest);
        var archiveBytes = CreateArchive(
            manifestBytes,
            macros.BytesByPath);
        var manifestJson = StrictUtf8.GetString(manifestBytes);
        return new PortableExportPackage(
            archiveBytes,
            manifestJson,
            projectedTemplates.Count,
            macros.ManifestMacros.Count,
            namedDestinations.Count,
            presets.Count,
            new PortablePackageOmissionSummary(
                omittedNamedDestinations,
                omittedTemplateDestinations),
            macros.ManifestMacros
                .Where(macro => macro.HasKeyboardEvents)
                .Select(macro => macro.ContentId)
                .ToArray());
    }

    private static bool IsTemplateDestinationDependency(
        NamedDestination candidate,
        IReadOnlyList<SessionTemplate> selectedTemplates)
    {
        var assignedAccountKeys = candidate.AccountKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        if (assignedAccountKeys.Count == 0)
            return false;

        return selectedTemplates
            .SelectMany(template => template.ClientSlots)
            .Any(slot =>
                assignedAccountKeys.Contains(slot.AccountKey) &&
                AreEquivalentDestinations(
                    candidate.Value,
                    slot.Destination));
    }

    private static bool AreEquivalentDestinations(
        string first,
        string? second)
    {
        if (string.IsNullOrWhiteSpace(second))
            return false;
        if (string.Equals(
                first,
                second.Trim(),
                StringComparison.Ordinal))
        {
            return true;
        }

        return TryProjectPublicPlace(first, out var firstPlaceId) &&
            TryProjectPublicPlace(second, out var secondPlaceId) &&
            firstPlaceId == secondPlaceId;
    }

    internal static PortableImportPlan PrepareImport(
        ReadOnlySpan<byte> archiveContents,
        AppSettings currentSettings,
        SessionTemplateCatalog currentCatalog) =>
        PrepareImport(
            archiveContents,
            currentSettings,
            currentCatalog,
            currentDisplay: null);

    internal static PortableImportPlan PrepareImport(
        ReadOnlySpan<byte> archiveContents,
        AppSettings currentSettings,
        SessionTemplateCatalog currentCatalog,
        ExactWheelDisplayTopology? currentDisplay)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);
        ArgumentNullException.ThrowIfNull(currentCatalog);
        var archive = ReadArchive(archiveContents);
        var manifest = DeserializeManifest(archive.ManifestBytes);
        ValidateManifestStructure(manifest);
        var importedMacros = ValidateImportedMacros(
            manifest.Macros!,
            archive.MacroBytesByPath);
        ValidateTemplateMacroReferences(manifest, importedMacros);
        return BuildImportPlan(
            manifest,
            importedMacros,
            currentSettings,
            currentCatalog,
            currentDisplay);
    }

    internal static async Task<byte[]> ReadPackageFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException(
                "The portable package could not be found.",
                fullPath,
                exception);
        }
        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "Portable packages must be regular local files.");
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumArchiveBytes)
            throw new InvalidDataException("The portable archive is too large.");
        if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException(
                "The portable package path changed while it was opened.");
        }
        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaximumArchiveBytes)
                throw new InvalidDataException("The portable archive is too large.");
            output.Write(buffer, 0, read);
        }
    }

    internal static async Task WritePackageFileAsync(
        string path,
        PortableExportPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        if (package.ArchiveBytes.Length is 0 or > MaximumArchiveBytes)
            throw new InvalidDataException("The portable archive is too large.");

        var fullPath = Path.GetFullPath(path);
        var destinationExists = ValidatePortableOutputPath(fullPath);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new DirectoryNotFoundException(
                "The selected portable-package folder does not exist.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan |
                             FileOptions.WriteThrough))
            {
                await stream.WriteAsync(
                        package.ArchiveBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var stillExists = ValidatePortableOutputPath(fullPath);
            if (destinationExists != stillExists)
            {
                throw new IOException(
                    "The portable-package destination changed before commit.");
            }
            if (destinationExists)
            {
                File.Replace(
                    temporaryPath,
                    fullPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
            temporaryPath = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                        System.Security.SecurityException)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"Portable export cleanup failed: {exception.GetType().Name}.");
                }
            }
        }
    }

    private static bool ValidatePortableOutputPath(string fullPath)
    {
        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException(
                    "The portable-package destination is a directory.");
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Portable packages cannot be written through file-system links.");
            }
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory) ||
                !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    "The selected portable-package folder does not exist.");
            }
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Portable packages cannot be written through file-system links.");
            }
            return false;
        }
    }

    internal static PortablePackageApplyResult CloneApplyResult(
        PortablePackageApplyResult source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new PortablePackageApplyResult(
            AppSettingsSnapshot.Create(source.Settings),
            SessionTemplatePolicy.Normalize(source.Catalog),
            source.MacroBlobs.Select(blob => blob with
            {
                Bytes = [.. blob.Bytes],
                RecordedDisplay = CloneDisplay(blob.RecordedDisplay)
            }).ToArray());
    }

    private static PreparedExportMacros PrepareExportMacros(
        IEnumerable<MacroDefinition> definitions,
        IReadOnlyDictionary<string, AccountProfile> accountByKey,
        Func<MacroDefinition, byte[]> readMacroBytes)
    {
        var manifestMacros = new List<PortableManifestMacro>();
        var bytesByPath = new Dictionary<string, byte[]>(
            StringComparer.Ordinal);
        var sourceContentIdMap = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var macrosByIdentity = new Dictionary<MacroIdentity, PortableManifestMacro>();
        foreach (var definition in definitions.OrderBy(
                     item => item.ContentId,
                     StringComparer.OrdinalIgnoreCase))
        {
            var bytes = readMacroBytes(definition) ??
                throw new InvalidDataException(
                    $"Macro '{definition.ContentId}' returned no payload.");
            var verified = VerifyMacroPayload(definition, bytes);
            var identity = new MacroIdentity(
                definition.Kind,
                verified.Sha256);
            if (macrosByIdentity.TryGetValue(identity, out var existing))
            {
                sourceContentIdMap.Add(
                    definition.ContentId,
                    existing.ContentId);
                continue;
            }

            long? recordedUserId = null;
            if (definition.Kind == SessionMacroKind.Client &&
                !string.IsNullOrWhiteSpace(definition.RecordedAccountKey) &&
                accountByKey.TryGetValue(
                    definition.RecordedAccountKey,
                    out var account))
            {
                recordedUserId = account.UserId;
            }
            var portable = new PortableManifestMacro
            {
                ContentId = GetCanonicalContentId(
                    verified.Sha256,
                    definition.Kind),
                Name = definition.Name,
                Kind = definition.Kind,
                Path = GetMacroPath(verified.Sha256),
                Sha256 = verified.Sha256,
                Size = bytes.LongLength,
                DurationMilliseconds = verified.DurationMilliseconds,
                EventCount = verified.Recording.Events.Count,
                HasKeyboardEvents = verified.HasKeyboardEvents,
                RecordedForRobloxUserId = recordedUserId
            };
            macrosByIdentity.Add(identity, portable);
            manifestMacros.Add(portable);
            bytesByPath.TryAdd(portable.Path, [.. bytes]);
            sourceContentIdMap.Add(
                definition.ContentId,
                portable.ContentId);
        }

        return new PreparedExportMacros(
            manifestMacros,
            bytesByPath,
            sourceContentIdMap);
    }

    private static PortableManifestTemplate ProjectTemplate(
        SessionTemplate source,
        int portableIndex,
        IReadOnlyDictionary<string, AccountProfile> accountByKey,
        IReadOnlyDictionary<string, string> macroIdMap,
        ref int omittedDestinationCount)
    {
        var slots = new List<PortableManifestTemplateSlot>();
        foreach (var sourceSlot in source.ClientSlots.OrderBy(slot => slot.Order))
        {
            if (!accountByKey.TryGetValue(sourceSlot.AccountKey, out var account))
            {
                throw new InvalidDataException(
                    $"Template '{source.Name}' references an unavailable account.");
            }
            long? destinationPlaceId = null;
            if (!string.IsNullOrWhiteSpace(sourceSlot.Destination))
            {
                if (TryProjectPublicPlace(
                        sourceSlot.Destination,
                        out var projectedPlaceId))
                {
                    destinationPlaceId = projectedPlaceId;
                }
                else
                {
                    omittedDestinationCount++;
                }
            }

            PortableManifestPlacement? placement = null;
            if (sourceSlot.Placement is not null)
            {
                placement = new PortableManifestPlacement
                {
                    MonitorOrdinal = sourceSlot.Placement.MonitorIndex,
                    Left = sourceSlot.Placement.Left,
                    Top = sourceSlot.Placement.Top,
                    Width = sourceSlot.Placement.Width,
                    Height = sourceSlot.Placement.Height
                };
            }

            slots.Add(new PortableManifestTemplateSlot
            {
                PortableId = $"slot-{slots.Count + 1}",
                RobloxUserId = account.UserId,
                Order = slots.Count,
                DestinationPlaceId = destinationPlaceId,
                Placement = placement,
                PerClientMacroContentId = MapMacroId(
                    sourceSlot.PerClientMacroId,
                    macroIdMap)
            });
        }

        List<long>? sharedTargets = null;
        if (source.SharedMacroAccountKeys is not null)
        {
            sharedTargets = source.SharedMacroAccountKeys.Select(key =>
                accountByKey.TryGetValue(key, out var account)
                    ? account.UserId
                    : throw new InvalidDataException(
                        $"Template '{source.Name}' has an unavailable shared-macro target."))
                .Distinct()
                .ToList();
        }

        return new PortableManifestTemplate
        {
            PortableId = $"template-{portableIndex}",
            Name = source.Name,
            DelaySeconds = source.DelaySeconds,
            LayoutMode = source.LayoutMode,
            MacroMode = source.MacroMode,
            ClientSlots = slots,
            SharedMacroContentId = MapMacroId(
                source.SharedMacroId,
                macroIdMap),
            SharedMacroAccountUserIds = sharedTargets,
            WholeLayoutMacroContentId = MapMacroId(
                source.WholeLayoutMacroId,
                macroIdMap),
            RepeatWholeLayoutMacro = source.RepeatWholeLayoutMacro,
            UpdatedAtUtc = source.UpdatedAtUtc.ToUniversalTime()
        };
    }

    private static string? MapMacroId(
        string? sourceId,
        IReadOnlyDictionary<string, string> macroIdMap)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return null;
        return macroIdMap.TryGetValue(sourceId, out var mapped)
            ? mapped
            : throw new InvalidDataException(
                $"Referenced macro '{sourceId}' was not expanded into the package.");
    }

    private static byte[] SerializeManifest(PortableManifest manifest)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            JsonOptions);
        if (serialized.Length + 1 > MaximumManifestBytes)
            throw new InvalidDataException("The portable manifest is too large.");
        var contents = new byte[serialized.Length + 1];
        serialized.CopyTo(contents, 0);
        contents[^1] = (byte)'\n';
        return contents;
    }

    private static byte[] CreateArchive(
        byte[] manifestBytes,
        IReadOnlyDictionary<string, byte[]> macroBytesByPath)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(
                   output,
                   ZipArchiveMode.Create,
                   leaveOpen: true,
                   entryNameEncoding: StrictUtf8))
        {
            WriteArchiveEntry(
                archive,
                ManifestEntryName,
                manifestBytes,
                CompressionLevel.Optimal);
            foreach (var item in macroBytesByPath.OrderBy(
                         item => item.Key,
                         StringComparer.Ordinal))
            {
                WriteArchiveEntry(
                    archive,
                    item.Key,
                    item.Value,
                    CompressionLevel.NoCompression);
            }
        }
        if (output.Length > MaximumArchiveBytes)
            throw new InvalidDataException("The portable archive is too large.");
        return output.ToArray();
    }

    private static void WriteArchiveEntry(
        ZipArchive archive,
        string name,
        byte[] contents,
        CompressionLevel compressionLevel)
    {
        var entry = archive.CreateEntry(name, compressionLevel);
        using var stream = entry.Open();
        stream.Write(contents);
    }

    private static ReadPortableArchiveResult ReadArchive(
        ReadOnlySpan<byte> archiveContents)
    {
        if (archiveContents.IsEmpty)
            throw new InvalidDataException("The portable archive is empty.");
        if (archiveContents.Length > MaximumArchiveBytes)
            throw new InvalidDataException("The portable archive is too large.");

        var copied = archiveContents.ToArray();
        try
        {
            using var input = new MemoryStream(copied, writable: false);
            using var archive = new ZipArchive(
                input,
                ZipArchiveMode.Read,
                leaveOpen: false,
                entryNameEncoding: StrictUtf8);
            if (archive.Entries.Count is 0 or > MaximumArchiveEntries)
            {
                throw new InvalidDataException(
                    "The portable archive has an invalid entry count.");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            byte[]? manifestBytes = null;
            var macros = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                ValidateArchiveEntryName(entry.FullName);
                if (!names.Add(entry.FullName))
                {
                    throw new InvalidDataException(
                        "The portable archive contains duplicate entry names.");
                }
                if (IsSymbolicLink(entry))
                {
                    throw new InvalidDataException(
                        "The portable archive contains a symbolic link.");
                }

                var maximum = entry.FullName.Equals(
                    ManifestEntryName,
                    StringComparison.Ordinal)
                    ? MaximumManifestBytes
                    : IsCanonicalMacroPath(entry.FullName)
                        ? ExactWheelLimits.MaximumMacroFileBytes
                        : throw new InvalidDataException(
                            $"Unknown portable archive entry '{entry.FullName}'.");
                if (entry.Length is <= 0 || entry.Length > maximum)
                {
                    throw new InvalidDataException(
                        $"Portable entry '{entry.FullName}' is outside its size boundary.");
                }
                expanded = checked(expanded + entry.Length);
                if (expanded > MaximumExpandedBytes)
                {
                    throw new InvalidDataException(
                        "The portable archive expands beyond its aggregate boundary.");
                }
                var bytes = ReadZipEntry(entry, maximum);
                if (entry.FullName == ManifestEntryName)
                    manifestBytes = bytes;
                else
                    macros.Add(entry.FullName, bytes);
            }

            return new ReadPortableArchiveResult(
                manifestBytes ?? throw new InvalidDataException(
                    "The portable archive is missing manifest.json."),
                macros);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or DecoderFallbackException or
                ArgumentException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException(
                "The portable archive could not be read safely.",
                exception);
        }
    }

    private static byte[] ReadZipEntry(ZipArchiveEntry entry, long maximum)
    {
        if (entry.Length > int.MaxValue)
            throw new InvalidDataException("A portable entry is too large.");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            if (output.Length + read > maximum)
            {
                throw new InvalidDataException(
                    $"Portable entry '{entry.FullName}' grew beyond its boundary.");
            }
            output.Write(buffer, 0, read);
        }
        if (output.Length != entry.Length)
        {
            throw new InvalidDataException(
                $"Portable entry '{entry.FullName}' length changed while reading.");
        }
        return output.ToArray();
    }

    private static void ValidateArchiveEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name.Length > 128 ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name[0] == '/' ||
            name[^1] == '/' ||
            name.Contains(':', StringComparison.Ordinal) ||
            name.Split('/').Any(segment =>
                segment is "" or "." or ".."))
        {
            throw new InvalidDataException(
                "The portable archive contains an unsafe entry path.");
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static bool IsCanonicalMacroPath(string path) =>
        path.Length == "macros/".Length + 64 + ".ewmacro".Length &&
        path.StartsWith("macros/", StringComparison.Ordinal) &&
        path.EndsWith(".ewmacro", StringComparison.Ordinal) &&
        path.AsSpan("macros/".Length, 64).IndexOfAnyExcept(
            LowerHexCharacters) < 0;

    private static PortableManifest DeserializeManifest(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumManifestBytes)
            throw new InvalidDataException("The portable manifest is too large.");
        try
        {
            _ = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 20
                });
            RejectDuplicateJsonProperties(document.RootElement);
            return JsonSerializer.Deserialize<PortableManifest>(
                       bytes,
                       JsonOptions) ??
                throw new InvalidDataException(
                    "The portable manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The portable manifest is not valid strict JSON.",
                exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The portable manifest is not valid UTF-8.",
                exception);
        }
    }

    private static void RejectDuplicateJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "The portable manifest contains a duplicate JSON property.");
                }
                RejectDuplicateJsonProperties(property.Value);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in element.EnumerateArray())
            RejectDuplicateJsonProperties(item);
    }

    private static void ValidateManifestStructure(PortableManifest manifest)
    {
        if (!string.Equals(manifest.Format, FormatName, StringComparison.Ordinal))
            throw new InvalidDataException("This is not a SessionDock portable package.");
        if (manifest.Version != CurrentVersion)
            throw new InvalidDataException("The portable package version is unsupported.");
        if (manifest.LayoutProfile is null || manifest.Omissions is null ||
            manifest.Macros is null || manifest.NamedDestinations is null ||
            manifest.BatchPresets is null || manifest.Templates is null)
        {
            throw new InvalidDataException(
                "The portable manifest is missing a required section.");
        }
        ValidateLayoutProfile(manifest.LayoutProfile);
        if (manifest.Omissions.NamedDestinations < 0 ||
            manifest.Omissions.TemplateSlotDestinations < 0)
        {
            throw new InvalidDataException(
                "The portable omission counts are invalid.");
        }
        if (manifest.Macros.Count > MaximumMacroEntries ||
            manifest.NamedDestinations.Count >
                NamedDestinationPolicy.MaximumDestinations ||
            manifest.BatchPresets.Count > BatchLaunchPreferences.MaximumPresets ||
            manifest.Templates.Count > SessionTemplatePolicy.MaximumTemplates)
        {
            throw new InvalidDataException(
                "The portable manifest exceeds an item-count boundary.");
        }
        ValidateManifestMacros(manifest.Macros);
        ValidateManifestDestinations(manifest.NamedDestinations);
        ValidateManifestPresets(manifest.BatchPresets);
        ValidateManifestTemplates(manifest.Templates);
    }

    private static void ValidateLayoutProfile(
        PortableManifestLayoutProfile profile)
    {
        var values = new[]
        {
            profile.TargetWidth,
            profile.TargetHeight,
            profile.MinimumWidth,
            profile.MinimumHeight,
            profile.RevealX,
            profile.RevealY
        };
        if (values.Any(value => !double.IsFinite(value) || value <= 0) ||
            profile.TargetWidth > 7680 || profile.TargetHeight > 4320 ||
            profile.MinimumWidth > profile.TargetWidth ||
            profile.MinimumHeight > profile.TargetHeight ||
            profile.RevealX > profile.TargetWidth ||
            profile.RevealY > profile.TargetHeight)
        {
            throw new InvalidDataException(
                "The portable layout profile is invalid.");
        }
    }

    private static void ValidateManifestMacros(
        IReadOnlyList<PortableManifestMacro> macros)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<MacroIdentity>();
        foreach (var macro in macros)
        {
            if (macro is null || !Enum.IsDefined(macro.Kind) ||
                !IsNormalizedName(
                    macro.Name,
                    SessionTemplatePolicy.MaximumNameLength) ||
                !IsLowerSha256(macro.Sha256) ||
                macro.ContentId != GetCanonicalContentId(
                    macro.Sha256,
                    macro.Kind) ||
                macro.Path != GetMacroPath(macro.Sha256) ||
                macro.Size is <
                    ExactWheelMacroSerializer.FixedHeaderBytes + sizeof(uint) or
                    > ExactWheelLimits.MaximumMacroFileBytes ||
                macro.DurationMilliseconds is < 0 or
                    > SessionTemplatePolicy.MaximumDurationMilliseconds ||
                macro.EventCount is <= 0 or
                    > SessionTemplatePolicy.MaximumEventCount ||
                macro.RecordedForRobloxUserId is <= 0 ||
                macro.Kind == SessionMacroKind.WholeLayout &&
                    macro.RecordedForRobloxUserId is not null ||
                !ids.Add(macro.ContentId) ||
                !identities.Add(new MacroIdentity(
                    macro.Kind,
                    macro.Sha256)))
            {
                throw new InvalidDataException(
                    "The portable manifest contains an invalid macro.");
            }
        }
    }

    private static void ValidateManifestDestinations(
        IReadOnlyList<PortableManifestNamedDestination> destinations)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in destinations)
        {
            if (destination is null ||
                !IsPortableId(destination.PortableId, "destination-") ||
                !ids.Add(destination.PortableId) ||
                !IsNormalizedName(
                    destination.Name,
                    NamedDestinationPolicy.MaximumNameLength) ||
                destination.PlaceId <= 0 ||
                !ValidateUserIds(destination.AccountUserIds, 0, 128))
            {
                throw new InvalidDataException(
                    "The portable manifest contains an invalid named destination.");
            }
        }
    }

    private static void ValidateManifestPresets(
        IReadOnlyList<PortableManifestBatchPreset> presets)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            if (preset is null ||
                !IsPortableId(preset.PortableId, "preset-") ||
                !ids.Add(preset.PortableId) ||
                BatchLaunchPreferences.NormalizePresetName(preset.Name) !=
                    preset.Name ||
                !BatchLaunchPreferences.SupportedDelaySeconds.Contains(
                    preset.DelaySeconds) ||
                !ValidateUserIds(
                    preset.AccountUserIds,
                    2,
                    BatchLaunchPreferences.MaximumAccountsPerPreset))
            {
                throw new InvalidDataException(
                    "The portable manifest contains an invalid batch preset.");
            }
        }
    }

    private static void ValidateManifestTemplates(
        IReadOnlyList<PortableManifestTemplate> templates)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in templates)
        {
            if (template is null ||
                !IsPortableId(template.PortableId, "template-") ||
                !ids.Add(template.PortableId) ||
                !IsNormalizedName(
                    template.Name,
                    SessionTemplatePolicy.MaximumNameLength) ||
                !BatchLaunchPreferences.SupportedDelaySeconds.Contains(
                    template.DelaySeconds) ||
                !Enum.IsDefined(template.LayoutMode) ||
                !Enum.IsDefined(template.MacroMode) ||
                template.ClientSlots is null ||
                template.ClientSlots.Count is 0 or >
                    SessionTemplatePolicy.MaximumSlotsPerTemplate ||
                template.UpdatedAtUtc <= DateTimeOffset.UnixEpoch)
            {
                throw new InvalidDataException(
                    "The portable manifest contains an invalid template.");
            }
            ValidateManifestTemplateSlots(template);
            ValidateTemplateMacroShape(template);
        }
    }

    private static void ValidateManifestTemplateSlots(
        PortableManifestTemplate template)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var users = new HashSet<long>();
        for (var index = 0; index < template.ClientSlots!.Count; index++)
        {
            var slot = template.ClientSlots[index];
            if (slot is null ||
                !IsPortableId(slot.PortableId, "slot-") ||
                !ids.Add(slot.PortableId) ||
                slot.RobloxUserId <= 0 ||
                !users.Add(slot.RobloxUserId) ||
                slot.Order != index ||
                slot.DestinationPlaceId is <= 0)
            {
                throw new InvalidDataException(
                    "The portable manifest contains an invalid template slot.");
            }
            if (slot.Placement is not null)
                ValidatePortablePlacement(slot.Placement);
        }
    }

    private static void ValidatePortablePlacement(
        PortableManifestPlacement placement)
    {
        if (placement.MonitorOrdinal is < 0 or >
                SessionTemplatePolicy.MaximumMonitorIndex ||
            !double.IsFinite(placement.Left) ||
            !double.IsFinite(placement.Top) ||
            !double.IsFinite(placement.Width) ||
            !double.IsFinite(placement.Height) ||
            placement.Left < 0 || placement.Top < 0 ||
            placement.Width <= 0 || placement.Height <= 0 ||
            placement.Left + placement.Width > 1.0000001 ||
            placement.Top + placement.Height > 1.0000001)
        {
            throw new InvalidDataException(
                "The portable template placement is invalid.");
        }
    }

    private static void ValidateTemplateMacroShape(
        PortableManifestTemplate template)
    {
        var slots = template.ClientSlots!;
        switch (template.MacroMode)
        {
            case SessionTemplateMacroMode.None:
                if (template.SharedMacroContentId is not null ||
                    template.SharedMacroAccountUserIds is not null ||
                    template.WholeLayoutMacroContentId is not null ||
                    template.RepeatWholeLayoutMacro ||
                    slots.Any(slot => slot.PerClientMacroContentId is not null))
                {
                    throw InvalidMacroShape();
                }
                break;
            case SessionTemplateMacroMode.PerClient:
                if (template.SharedMacroContentId is not null ||
                    template.SharedMacroAccountUserIds is not null ||
                    template.WholeLayoutMacroContentId is not null ||
                    template.RepeatWholeLayoutMacro ||
                    !slots.Any(slot => slot.PerClientMacroContentId is not null))
                {
                    throw InvalidMacroShape();
                }
                break;
            case SessionTemplateMacroMode.Shared:
                if (string.IsNullOrWhiteSpace(template.SharedMacroContentId) ||
                    template.WholeLayoutMacroContentId is not null ||
                    template.RepeatWholeLayoutMacro ||
                    slots.Any(slot => slot.PerClientMacroContentId is not null))
                {
                    throw InvalidMacroShape();
                }
                if (template.SharedMacroAccountUserIds is not null &&
                    (!ValidateUserIds(
                        template.SharedMacroAccountUserIds,
                        1,
                        slots.Count) ||
                     template.SharedMacroAccountUserIds.Any(userId =>
                         slots.All(slot => slot.RobloxUserId != userId))))
                {
                    throw InvalidMacroShape();
                }
                break;
            case SessionTemplateMacroMode.WholeLayout:
                if (template.SharedMacroContentId is not null ||
                    template.SharedMacroAccountUserIds is not null ||
                    string.IsNullOrWhiteSpace(
                        template.WholeLayoutMacroContentId) ||
                    slots.Any(slot => slot.PerClientMacroContentId is not null))
                {
                    throw InvalidMacroShape();
                }
                break;
            default:
                throw InvalidMacroShape();
        }
    }

    private static InvalidDataException InvalidMacroShape() =>
        new("The portable template macro assignment shape is invalid.");

    private static IReadOnlyDictionary<string, ValidatedImportedMacro>
        ValidateImportedMacros(
        IReadOnlyList<PortableManifestMacro> manifestMacros,
        IReadOnlyDictionary<string, byte[]> archiveMacros)
    {
        var expectedPaths = manifestMacros
            .Select(macro => macro.Path)
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedPaths.SetEquals(archiveMacros.Keys))
        {
            throw new InvalidDataException(
                "The portable macro entries do not match the manifest.");
        }

        var result = new Dictionary<string, ValidatedImportedMacro>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var macro in manifestMacros)
        {
            var bytes = archiveMacros[macro.Path];
            if (bytes.LongLength != macro.Size)
            {
                throw new InvalidDataException(
                    $"Macro '{macro.ContentId}' size does not match its manifest.");
            }
            var definition = new MacroDefinition
            {
                ContentId = macro.ContentId,
                SafeFileName = Path.GetFileName(macro.Path),
                Name = macro.Name,
                Kind = macro.Kind,
                DurationMilliseconds = macro.DurationMilliseconds,
                EventCount = macro.EventCount,
                Sha256 = macro.Sha256,
                RecordedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1)
            };
            var verified = VerifyMacroPayload(definition, bytes);
            if (verified.HasKeyboardEvents != macro.HasKeyboardEvents)
            {
                throw new InvalidDataException(
                    $"Macro '{macro.ContentId}' keyboard-event flag is incorrect.");
            }
            result.Add(
                macro.ContentId,
                new ValidatedImportedMacro(
                    macro,
                    [.. bytes],
                    verified.Recording));
        }
        return result;
    }

    private static void ValidateTemplateMacroReferences(
        PortableManifest manifest,
        IReadOnlyDictionary<string, ValidatedImportedMacro> macros)
    {
        foreach (var template in manifest.Templates!)
        {
            foreach (var slot in template.ClientSlots!)
            {
                if (slot.PerClientMacroContentId is not null)
                {
                    RequireMacroKind(
                        slot.PerClientMacroContentId,
                        SessionMacroKind.Client,
                        macros);
                }
            }
            if (template.SharedMacroContentId is not null)
            {
                RequireMacroKind(
                    template.SharedMacroContentId,
                    SessionMacroKind.Client,
                    macros);
            }
            if (template.WholeLayoutMacroContentId is not null)
            {
                RequireMacroKind(
                    template.WholeLayoutMacroContentId,
                    SessionMacroKind.WholeLayout,
                    macros);
            }
        }
    }

    private static void RequireMacroKind(
        string contentId,
        SessionMacroKind kind,
        IReadOnlyDictionary<string, ValidatedImportedMacro> macros)
    {
        if (!macros.TryGetValue(contentId, out var macro) ||
            macro.Manifest.Kind != kind)
        {
            throw new InvalidDataException(
                "A portable template references a missing or incompatible macro.");
        }
    }

    private static PortableImportPlan BuildImportPlan(
        PortableManifest manifest,
        IReadOnlyDictionary<string, ValidatedImportedMacro> importedMacros,
        AppSettings currentSettings,
        SessionTemplateCatalog currentCatalog,
        ExactWheelDisplayTopology? currentDisplay)
    {
        var settings = AppSettingsSnapshot.Create(currentSettings);
        var catalog = SessionTemplatePolicy.Normalize(currentCatalog);
        var accountsByUserId = settings.Accounts
            .Where(account => account.UserId > 0)
            .GroupBy(account => account.UserId)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var unmatchedAccountReferences = 0;

        var localMacroIdMap = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var blobs = new List<PortableMacroBlob>();
        var importedMacroDefinitions = 0;
        var deduplicatedMacros = 0;
        var usedMacroNames = catalog.MacroDefinitions
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var imported in importedMacros.Values.OrderBy(
                     macro => macro.Manifest.ContentId,
                     StringComparer.OrdinalIgnoreCase))
        {
            var existing = catalog.MacroDefinitions.FirstOrDefault(definition =>
                definition.Kind == imported.Manifest.Kind &&
                string.Equals(
                    definition.Sha256,
                    imported.Manifest.Sha256,
                    StringComparison.OrdinalIgnoreCase));
            var needsDefinition = existing is null;
            MacroDefinition definition;
            if (existing is not null)
            {
                definition = existing;
                deduplicatedMacros++;
            }
            else
            {
                if (catalog.MacroDefinitions.Count >=
                    SessionTemplatePolicy.MaximumMacroDefinitions)
                {
                    throw new InvalidDataException(
                        "The local macro catalog has no room for this package.");
                }
                string? recordedAccountKey = null;
                if (imported.Manifest.RecordedForRobloxUserId is long userId)
                {
                    if (accountsByUserId.TryGetValue(userId, out var account))
                        recordedAccountKey = account.Key;
                    else
                        unmatchedAccountReferences++;
                }
                definition = new MacroDefinition
                {
                    ContentId = imported.Manifest.ContentId,
                    SafeFileName = Path.GetFileName(imported.Manifest.Path),
                    Name = CreateImportedName(
                        imported.Manifest.Name,
                        SessionTemplatePolicy.MaximumNameLength,
                        usedMacroNames),
                    Kind = imported.Manifest.Kind,
                    RecordedAccountKey = recordedAccountKey,
                    DurationMilliseconds =
                        imported.Manifest.DurationMilliseconds,
                    EventCount = imported.Manifest.EventCount,
                    Sha256 = imported.Manifest.Sha256.ToUpperInvariant(),
                    RecordedAtUtc = DateTimeOffset.UtcNow
                };
                catalog.MacroDefinitions.Add(definition);
                importedMacroDefinitions++;
            }
            localMacroIdMap.Add(
                imported.Manifest.ContentId,
                definition.ContentId);
            blobs.Add(new PortableMacroBlob(
                definition.ContentId,
                Path.GetFileName(imported.Manifest.Path),
                imported.Manifest.Sha256,
                imported.Manifest.Kind,
                [.. imported.Bytes],
                needsDefinition,
                imported.Recording.Display.Monitors.Count,
                imported.Recording.Display.VirtualWidth,
                imported.Recording.Display.VirtualHeight,
                imported.Manifest.HasKeyboardEvents,
                CloneDisplay(imported.Recording.Display)));
        }

        var importedDestinations = ImportNamedDestinations(
            manifest.NamedDestinations!,
            settings,
            accountsByUserId,
            ref unmatchedAccountReferences);
        var importedPresets = ImportBatchPresets(
            manifest.BatchPresets!,
            settings,
            accountsByUserId,
            ref unmatchedAccountReferences);
        var templateResult = ImportTemplates(
            manifest.Templates!,
            catalog,
            accountsByUserId,
            localMacroIdMap,
            importedMacros,
            currentDisplay,
            ref unmatchedAccountReferences);

        catalog = SessionTemplatePolicy.Normalize(catalog);
        _ = NamedDestinationPolicy.NormalizeInPlace(settings);
        settings.BatchLaunchPresets = BatchLaunchPreferences.NormalizePresets(
                settings.BatchLaunchPresets,
                settings.Accounts)
            .ToList();
        var applyResult = new PortablePackageApplyResult(
            settings,
            catalog,
            blobs);
        return new PortableImportPlan(
            applyResult,
            new PortableLayoutProfile(
                manifest.LayoutProfile!.TargetWidth,
                manifest.LayoutProfile.TargetHeight,
                manifest.LayoutProfile.MinimumWidth,
                manifest.LayoutProfile.MinimumHeight,
                manifest.LayoutProfile.RevealX,
                manifest.LayoutProfile.RevealY),
            new PortablePackageOmissionSummary(
                manifest.Omissions!.NamedDestinations,
                manifest.Omissions.TemplateSlotDestinations),
            importedMacros.Values
                .Where(macro => macro.Manifest.HasKeyboardEvents)
                .Select(macro => macro.Manifest.ContentId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray(),
            templateResult.WholeLayoutAssignments,
            templateResult.ImportedCount,
            templateResult.SkippedCount,
            importedMacroDefinitions,
            deduplicatedMacros,
            importedDestinations,
            importedPresets,
            unmatchedAccountReferences);
    }

    private static int ImportNamedDestinations(
        IReadOnlyList<PortableManifestNamedDestination> imported,
        AppSettings settings,
        IReadOnlyDictionary<long, AccountProfile> accountsByUserId,
        ref int unmatchedAccountReferences)
    {
        var usedNames = settings.NamedDestinations
            .Select(destination => destination.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignedKeys = settings.NamedDestinations
            .SelectMany(destination => destination.AccountKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var source in imported)
        {
            if (settings.NamedDestinations.Count >=
                NamedDestinationPolicy.MaximumDestinations)
            {
                break;
            }
            var accountKeys = new List<string>();
            foreach (var userId in source.AccountUserIds!)
            {
                if (!accountsByUserId.TryGetValue(userId, out var account))
                {
                    unmatchedAccountReferences++;
                    continue;
                }
                if (!assignedKeys.Contains(account.Key) &&
                    string.IsNullOrWhiteSpace(account.Destination))
                {
                    assignedKeys.Add(account.Key);
                    accountKeys.Add(account.Key);
                    account.Destination = source.PlaceId.ToString(
                        CultureInfo.InvariantCulture);
                }
            }
            settings.NamedDestinations.Add(new NamedDestination
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = CreateImportedName(
                    source.Name,
                    NamedDestinationPolicy.MaximumNameLength,
                    usedNames),
                Value = source.PlaceId.ToString(CultureInfo.InvariantCulture),
                AccountKeys = accountKeys
            });
            count++;
        }
        return count;
    }

    private static int ImportBatchPresets(
        IReadOnlyList<PortableManifestBatchPreset> imported,
        AppSettings settings,
        IReadOnlyDictionary<long, AccountProfile> accountsByUserId,
        ref int unmatchedAccountReferences)
    {
        var usedNames = settings.BatchLaunchPresets
            .Select(preset => preset.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var source in imported)
        {
            if (settings.BatchLaunchPresets.Count >=
                BatchLaunchPreferences.MaximumPresets)
            {
                break;
            }
            var keys = new List<string>();
            foreach (var userId in source.AccountUserIds!)
            {
                if (accountsByUserId.TryGetValue(userId, out var account))
                    keys.Add(account.Key);
                else
                    unmatchedAccountReferences++;
            }
            keys = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (keys.Count < 2)
                continue;
            settings.BatchLaunchPresets.Add(new BatchLaunchPreset
            {
                Name = CreateImportedName(
                    source.Name,
                    BatchLaunchPreferences.MaximumPresetNameLength,
                    usedNames),
                AccountKeys = keys,
                DelaySeconds = source.DelaySeconds
            });
            count++;
        }
        return count;
    }

    private static ImportedTemplateResult ImportTemplates(
        IReadOnlyList<PortableManifestTemplate> imported,
        SessionTemplateCatalog catalog,
        IReadOnlyDictionary<long, AccountProfile> accountsByUserId,
        IReadOnlyDictionary<string, string> localMacroIdMap,
        IReadOnlyDictionary<string, ValidatedImportedMacro> importedMacros,
        ExactWheelDisplayTopology? currentDisplay,
        ref int unmatchedAccountReferences)
    {
        var usedNames = catalog.Templates
            .Select(template => template.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wholeAssignments = new List<PortableWholeLayoutAssignment>();
        var importedCount = 0;
        var skippedCount = 0;
        foreach (var source in imported)
        {
            var missingUsers = source.ClientSlots!
                .Select(slot => slot.RobloxUserId)
                .Where(userId => !accountsByUserId.ContainsKey(userId))
                .Distinct()
                .ToArray();
            if (missingUsers.Length > 0 ||
                catalog.Templates.Count >= SessionTemplatePolicy.MaximumTemplates)
            {
                unmatchedAccountReferences += missingUsers.Length;
                skippedCount++;
                continue;
            }

            var templateId = Guid.NewGuid().ToString("N");
            var slots = source.ClientSlots!.Select(slot =>
            {
                var account = accountsByUserId[slot.RobloxUserId];
                return new SessionTemplateClientSlot
                {
                    SlotId = Guid.NewGuid().ToString("N"),
                    AccountKey = account.Key,
                    Order = slot.Order,
                    Destination = slot.DestinationPlaceId?.ToString(
                        CultureInfo.InvariantCulture),
                    Placement = slot.Placement is null
                        ? null
                        : new NormalizedClientWindowPlacement
                        {
                            MonitorStableId = null,
                            MonitorDeviceName = null,
                            MonitorIndex = slot.Placement.MonitorOrdinal,
                            Left = slot.Placement.Left,
                            Top = slot.Placement.Top,
                            Width = slot.Placement.Width,
                            Height = slot.Placement.Height
                        },
                    PerClientMacroId = ResolveImportedMacroId(
                        slot.PerClientMacroContentId,
                        localMacroIdMap)
                };
            }).ToList();
            List<string>? sharedTargets = null;
            if (source.SharedMacroAccountUserIds is not null)
            {
                sharedTargets = source.SharedMacroAccountUserIds
                    .Select(userId => accountsByUserId[userId].Key)
                    .ToList();
            }
            var template = new SessionTemplate
            {
                SchemaVersion = SessionTemplatePolicy.TemplateSchemaVersion,
                Id = templateId,
                Name = CreateImportedName(
                    source.Name,
                    SessionTemplatePolicy.MaximumNameLength,
                    usedNames),
                DelaySeconds = source.DelaySeconds,
                LayoutMode = source.LayoutMode,
                MacroMode = source.MacroMode,
                ClientSlots = slots,
                SharedMacroId = ResolveImportedMacroId(
                    source.SharedMacroContentId,
                    localMacroIdMap),
                SharedMacroAccountKeys = sharedTargets,
                WholeLayoutMacroId = ResolveImportedMacroId(
                    source.WholeLayoutMacroContentId,
                    localMacroIdMap),
                RepeatWholeLayoutMacro = source.RepeatWholeLayoutMacro,
                UpdatedAtUtc = source.UpdatedAtUtc.ToUniversalTime(),
                LegacyPresetName = null
            };
            PortableMacroAdaptationResult? adaptation = null;
            if (source.WholeLayoutMacroContentId is not null &&
                currentDisplay is not null)
            {
                adaptation = PortableDeviceAdaptationPolicy
                    .ForWholeLayoutMacro(
                        importedMacros[source.WholeLayoutMacroContentId]
                            .Recording.Display,
                        currentDisplay);
                if (!adaptation.CanAssign)
                {
                    template.MacroMode = SessionTemplateMacroMode.None;
                    template.WholeLayoutMacroId = null;
                    template.RepeatWholeLayoutMacro = false;
                }
            }
            catalog.Templates.Add(template);
            importedCount++;
            if (source.WholeLayoutMacroContentId is not null)
            {
                var macro = importedMacros[source.WholeLayoutMacroContentId];
                wholeAssignments.Add(new PortableWholeLayoutAssignment(
                    templateId,
                    ResolveImportedMacroId(
                        source.WholeLayoutMacroContentId,
                        localMacroIdMap)!,
                    macro.Recording.Display.Monitors.Count,
                    macro.Recording.Display.VirtualWidth,
                    macro.Recording.Display.VirtualHeight,
                    CloneDisplay(macro.Recording.Display),
                    adaptation?.CanAssign != false,
                    adaptation?.Reasons.ToArray() ?? []));
            }
        }
        return new ImportedTemplateResult(
            importedCount,
            skippedCount,
            wholeAssignments);
    }

    private static string? ResolveImportedMacroId(
        string? portableId,
        IReadOnlyDictionary<string, string> localMacroIdMap)
    {
        if (portableId is null)
            return null;
        return localMacroIdMap.TryGetValue(portableId, out var localId)
            ? localId
            : throw new InvalidDataException(
                "An imported template macro could not be resolved.");
    }

    private static string CreateImportedName(
        string source,
        int maximumLength,
        ISet<string> usedNames)
    {
        if (usedNames.Add(source))
            return source;
        for (var number = 1; number <= 10_000; number++)
        {
            var suffix = number == 1
                ? " (imported)"
                : $" (imported {number})";
            var prefixLength = Math.Min(
                source.Length,
                maximumLength - suffix.Length);
            if (prefixLength > 0 &&
                prefixLength < source.Length &&
                char.IsHighSurrogate(source[prefixLength - 1]) &&
                char.IsLowSurrogate(source[prefixLength]))
            {
                prefixLength--;
            }
            var candidate = source[..Math.Max(0, prefixLength)] + suffix;
            if (usedNames.Add(candidate))
                return candidate;
        }
        throw new InvalidDataException(
            "A unique imported name could not be generated.");
    }

    private static VerifiedMacroPayload VerifyMacroPayload(
        MacroDefinition definition,
        byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bytes);
        if (!Enum.IsDefined(definition.Kind) ||
            bytes.LongLength > ExactWheelLimits.MaximumMacroFileBytes)
        {
            throw new InvalidDataException("The macro payload is invalid.");
        }
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
        if (!string.Equals(
                definition.Sha256,
                sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                definition.SafeFileName,
                Path.GetFileName(GetMacroPath(sha256)),
                StringComparison.OrdinalIgnoreCase) ||
            !IsSupportedSourceContentId(
                definition.ContentId,
                sha256,
                definition.Kind))
        {
            throw new InvalidDataException(
                "The macro payload identity does not match its catalog definition.");
        }
        var recording = ExactWheelMacroSerializer.Deserialize(bytes);
        ExactWheelRecordingValidator.ValidatePlayable(recording);
        var durationMilliseconds = checked((long)(
            (recording.DurationMicroseconds + 999UL) / 1_000UL));
        if (recording.Events.Count != definition.EventCount ||
            durationMilliseconds != definition.DurationMilliseconds)
        {
            throw new InvalidDataException(
                "The macro payload metadata does not match its catalog definition.");
        }
        return new VerifiedMacroPayload(
            sha256,
            durationMilliseconds,
            recording,
            recording.Events.Any(inputEvent => inputEvent.IsKeyboardEvent));
    }

    private static bool IsSupportedSourceContentId(
        string contentId,
        string sha256,
        SessionMacroKind kind) =>
        string.Equals(
            contentId,
            GetCanonicalContentId(sha256, kind),
            StringComparison.Ordinal) ||
        string.Equals(
            contentId,
            "ew-" + sha256,
            StringComparison.Ordinal);

    private static string GetCanonicalContentId(
        string sha256,
        SessionMacroKind kind) => kind switch
        {
            SessionMacroKind.Client => "ew-client-" + sha256,
            SessionMacroKind.WholeLayout => "ew-whole-layout-" + sha256,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static string GetMacroPath(string sha256) =>
        "macros/" + sha256 + ExactWheelMacroStore.MacroFileExtension;

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryProjectPublicPlace(
        string? destination,
        out long placeId)
    {
        placeId = 0;
        return !string.IsNullOrWhiteSpace(destination) &&
            DestinationParser.TryParse(destination, out var target, out _) &&
            target is { IsPrivateServer: false, PlaceId: > 0 } &&
            (placeId = target.PlaceId) > 0;
    }

    private static IReadOnlyDictionary<string, AccountProfile>
        BuildAccountByKey(IEnumerable<AccountProfile> accounts) => accounts
            .Where(account => account is not null &&
                account.UserId > 0 &&
                !string.IsNullOrWhiteSpace(account.Key))
            .GroupBy(account => account.UserId)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, MacroDefinition>
        BuildDefinitionsById(SessionTemplateCatalog catalog) =>
        catalog.MacroDefinitions
            .GroupBy(
                definition => definition.ContentId,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<SessionTemplate> SelectTemplates(
        SessionTemplateCatalog catalog,
        IReadOnlyList<string> selectedIds)
    {
        var byId = catalog.Templates.ToDictionary(
            template => template.Id,
            StringComparer.OrdinalIgnoreCase);
        return selectedIds.Select(id =>
            byId.TryGetValue(id, out var template)
                ? template
                : throw new InvalidDataException(
                    $"The selected template '{id}' is unavailable."))
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeSelection(
        IEnumerable<string>? values,
        int maximum,
        string description)
    {
        var result = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            var normalized = value?.Trim();
            if (string.IsNullOrEmpty(normalized) ||
                normalized.Length > SessionTemplatePolicy.MaximumIdentifierLength)
            {
                throw new ArgumentException(
                    $"A selected {description} ID is invalid.");
            }
            if (used.Add(normalized))
                result.Add(normalized);
            if (result.Count > maximum)
            {
                throw new ArgumentException(
                    $"Too many {description} IDs were selected.");
            }
        }
        return result;
    }

    private static bool IsPortableId(string? value, string prefix) =>
        value is { Length: > 0 and <= 80 } &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).Length > 0 &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept(DecimalCharacters) < 0;

    private static bool IsNormalizedName(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        value == value.Trim() &&
        !value.Any(character => char.IsControl(character));

    private static string NormalizePortableName(
        string? value,
        int maximumLength)
    {
        var normalized = SessionTemplatePolicy.NormalizeMacroName(value);
        if (normalized is null)
            throw new InvalidDataException("A portable item name is invalid.");
        if (normalized.Length <= maximumLength)
            return normalized;
        var length = maximumLength;
        if (length < normalized.Length &&
            char.IsHighSurrogate(normalized[length - 1]) &&
            char.IsLowSurrogate(normalized[length]))
        {
            length--;
        }
        return normalized[..length];
    }

    private static bool ValidateUserIds(
        IReadOnlyList<long>? userIds,
        int minimum,
        int maximum) =>
        userIds is not null &&
        userIds.Count >= minimum &&
        userIds.Count <= maximum &&
        userIds.All(userId => userId > 0) &&
        userIds.Distinct().Count() == userIds.Count;

    private static ExactWheelDisplayTopology CloneDisplay(
        ExactWheelDisplayTopology source) => new(
            source.VirtualLeft,
            source.VirtualTop,
            source.VirtualWidth,
            source.VirtualHeight,
            source.Monitors.Select(monitor => new ExactWheelMonitorSnapshot(
                monitor.Bounds,
                monitor.DpiX,
                monitor.DpiY)));

    private sealed record PreparedExportMacros(
        List<PortableManifestMacro> ManifestMacros,
        Dictionary<string, byte[]> BytesByPath,
        Dictionary<string, string> SourceContentIdMap);

    private sealed record VerifiedMacroPayload(
        string Sha256,
        long DurationMilliseconds,
        ExactWheelRecording Recording,
        bool HasKeyboardEvents);

    private sealed record ValidatedImportedMacro(
        PortableManifestMacro Manifest,
        byte[] Bytes,
        ExactWheelRecording Recording);

    private sealed record ReadPortableArchiveResult(
        byte[] ManifestBytes,
        IReadOnlyDictionary<string, byte[]> MacroBytesByPath);

    private sealed record ImportedTemplateResult(
        int ImportedCount,
        int SkippedCount,
        IReadOnlyList<PortableWholeLayoutAssignment> WholeLayoutAssignments);

    private readonly record struct MacroIdentity(
        SessionMacroKind Kind,
        string Sha256);

    private sealed class PortableManifest
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public PortableManifestLayoutProfile? LayoutProfile { get; set; }

        public PortableManifestOmissions? Omissions { get; set; }

        public List<PortableManifestMacro>? Macros { get; set; }

        public List<PortableManifestNamedDestination>? NamedDestinations
        { get; set; }

        public List<PortableManifestBatchPreset>? BatchPresets { get; set; }

        public List<PortableManifestTemplate>? Templates { get; set; }
    }

    private sealed class PortableManifestLayoutProfile
    {
        public double TargetWidth { get; set; }

        public double TargetHeight { get; set; }

        public double MinimumWidth { get; set; }

        public double MinimumHeight { get; set; }

        public double RevealX { get; set; }

        public double RevealY { get; set; }
    }

    private sealed class PortableManifestOmissions
    {
        public int NamedDestinations { get; set; }

        public int TemplateSlotDestinations { get; set; }
    }

    private sealed class PortableManifestMacro
    {
        public string ContentId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public SessionMacroKind Kind { get; set; }

        public string Path { get; set; } = string.Empty;

        public string Sha256 { get; set; } = string.Empty;

        public long Size { get; set; }

        public long DurationMilliseconds { get; set; }

        public int EventCount { get; set; }

        public bool HasKeyboardEvents { get; set; }

        public long? RecordedForRobloxUserId { get; set; }
    }

    private sealed class PortableManifestNamedDestination
    {
        public string PortableId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public long PlaceId { get; set; }

        public List<long>? AccountUserIds { get; set; }
    }

    private sealed class PortableManifestBatchPreset
    {
        public string PortableId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int DelaySeconds { get; set; }

        public List<long>? AccountUserIds { get; set; }
    }

    private sealed class PortableManifestTemplate
    {
        public string PortableId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int DelaySeconds { get; set; }

        public SessionTemplateLayoutMode LayoutMode { get; set; }

        public SessionTemplateMacroMode MacroMode { get; set; }

        public List<PortableManifestTemplateSlot>? ClientSlots { get; set; }

        public string? SharedMacroContentId { get; set; }

        public List<long>? SharedMacroAccountUserIds { get; set; }

        public string? WholeLayoutMacroContentId { get; set; }

        public bool RepeatWholeLayoutMacro { get; set; }

        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private sealed class PortableManifestTemplateSlot
    {
        public string PortableId { get; set; } = string.Empty;

        public long RobloxUserId { get; set; }

        public int Order { get; set; }

        public long? DestinationPlaceId { get; set; }

        public PortableManifestPlacement? Placement { get; set; }

        public string? PerClientMacroContentId { get; set; }
    }

    private sealed class PortableManifestPlacement
    {
        public int MonitorOrdinal { get; set; }

        public double Left { get; set; }

        public double Top { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }
    }
}
