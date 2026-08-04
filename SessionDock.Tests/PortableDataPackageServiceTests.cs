using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class PortableDataPackageServiceTests
{
    private const string SourceKeyA = "11111111111111111111111111111111";
    private const string SourceKeyB = "22222222222222222222222222222222";
    private const string TargetKeyA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TargetKeyB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PrivateCode = "PrivateCodeCanary_123456";

    [Fact]
    public void PrepareExport_SelectedGraphIsPortablePrivateDataIsOmittedAndBytesAreExact()
    {
        var macro = CreateMacro(
            "Keyboard macro",
            SessionMacroKind.Client,
            SourceKeyA,
            hasKeyboard: true);
        var settings = CreateSourceSettings();
        settings.NamedDestinations =
        [
            new NamedDestination
            {
                Id = "public-destination",
                Name = "Public place",
                Value = "12345",
                AccountKeys = [SourceKeyA]
            },
            new NamedDestination
            {
                Id = "private-destination",
                Name = "Private place",
                Value =
                    $"https://www.roblox.com/games/98765/X?privateServerLinkCode={PrivateCode}",
                AccountKeys = []
            }
        ];
        settings.BatchLaunchPresets =
        [
            new BatchLaunchPreset
            {
                Name = "Both accounts",
                AccountKeys = [SourceKeyA, SourceKeyB],
                DelaySeconds = 5
            }
        ];
        var catalog = CreateCatalog(macro);
        catalog.Templates[0].ClientSlots[0].Destination =
            $"https://www.roblox.com/games/555/X?privateServerLinkCode={PrivateCode}";

        var package = PortableDataPackageService.PrepareExport(
            settings,
            catalog,
            new PortablePackageSelection
            {
                TemplateIds = ["template-source"],
                NamedDestinationIds =
                    ["public-destination", "private-destination"],
                BatchPresetIds = ["Both accounts"]
            },
            _ => macro.Bytes);

        Assert.Equal("SessionDock-portable.sessiondock", PortableExportPackage.SuggestedFileName);
        Assert.Equal(1, package.TemplateCount);
        Assert.Equal(1, package.MacroCount);
        Assert.Equal(1, package.NamedDestinationCount);
        Assert.Equal(1, package.BatchPresetCount);
        Assert.Equal(1, package.Omissions.NamedDestinations);
        Assert.Equal(1, package.Omissions.TemplateSlotDestinations);
        Assert.True(package.ContainsKeyboardInput);
        Assert.Equal([macro.Definition.ContentId], package.KeyboardMacroContentIds);
        Assert.Contains("\"format\": \"sessiondock.portable\"", package.ManifestJson);
        Assert.Contains("\"version\": 1", package.ManifestJson);
        Assert.Contains("\"robloxUserId\": 101", package.ManifestJson);
        Assert.Contains("\"placeId\": 12345", package.ManifestJson);
        Assert.DoesNotContain(SourceKeyA, package.ManifestJson);
        Assert.DoesNotContain(SourceKeyB, package.ManifestJson);
        Assert.DoesNotContain("source-user-a", package.ManifestJson);
        Assert.DoesNotContain("Profiles", package.ManifestJson);
        Assert.DoesNotContain(PrivateCode, package.ManifestJson);
        Assert.DoesNotContain("MONITOR-SERIAL-CANARY", package.ManifestJson);
        Assert.DoesNotContain(@"\\.\DISPLAY9", package.ManifestJson);
        Assert.Contains("\"monitorOrdinal\": 1", package.ManifestJson);

        var entries = ReadArchive(package.ArchiveBytes);
        Assert.Equal(2, entries.Count);
        Assert.Equal(
            macro.Bytes,
            entries[$"macros/{macro.Sha256Lower}.ewmacro"]);
    }

    [Fact]
    public void PrepareExport_TemplateCarriesMatchingNamedDestinationAndAssignments()
    {
        var macro = CreateMacro(
            "Portable macro",
            SessionMacroKind.Client,
            SourceKeyA,
            hasKeyboard: false);
        var settings = CreateSourceSettings();
        settings.NamedDestinations =
        [
            new NamedDestination
            {
                Id = "template-destination",
                Name = "Farm server",
                Value = "12345",
                AccountKeys = [SourceKeyA]
            },
            new NamedDestination
            {
                Id = "unrelated-destination",
                Name = "Same place, different account",
                Value = "12345",
                AccountKeys = [SourceKeyB]
            }
        ];
        var catalog = CreateCatalog(macro);
        catalog.Templates[0].ClientSlots[0].Destination =
            "https://www.roblox.com/games/12345/Portable-Test";

        var package = PortableDataPackageService.PrepareExport(
            settings,
            catalog,
            new PortablePackageSelection
            {
                TemplateIds = ["template-source"]
            },
            _ => macro.Bytes);

        Assert.Equal(1, package.TemplateCount);
        Assert.Equal(1, package.NamedDestinationCount);
        Assert.Contains("Farm server", package.ManifestJson);
        Assert.DoesNotContain(
            "Same place, different account",
            package.ManifestJson);

        var plan = PortableDataPackageService.PrepareImport(
            package.ArchiveBytes,
            CreateTargetSettings(),
            SessionTemplatePolicy.CreateDefault(),
            ExactWheelTestData.Display());
        var imported = plan.Apply();

        var destination = Assert.Single(
            imported.Settings.NamedDestinations);
        Assert.Equal("Farm server", destination.Name);
        Assert.Equal("12345", destination.Value);
        Assert.Equal([TargetKeyA], destination.AccountKeys);
        var template = Assert.Single(imported.Catalog.Templates);
        var slot = Assert.Single(template.ClientSlots);
        Assert.Equal(TargetKeyA, slot.AccountKey);
        Assert.Equal("12345", slot.Destination);
    }

    [Fact]
    public void PrepareImport_MapsAccountsAddsConflictSafeItemsAndIsPure()
    {
        var macro = CreateMacro(
            "Shared name",
            SessionMacroKind.Client,
            SourceKeyA,
            hasKeyboard: false);
        var sourceSettings = CreateSourceSettings();
        sourceSettings.NamedDestinations =
        [
            new NamedDestination
            {
                Id = "public-destination",
                Name = "Shared name",
                Value = "12345",
                AccountKeys = [SourceKeyA]
            }
        ];
        sourceSettings.BatchLaunchPresets =
        [
            new BatchLaunchPreset
            {
                Name = "Shared name",
                AccountKeys = [SourceKeyA, SourceKeyB],
                DelaySeconds = 5
            }
        ];
        var sourceCatalog = CreateCatalog(macro);
        sourceCatalog.Templates[0].Name = "Shared name";
        sourceCatalog.Templates[0].ClientSlots[0].Destination = "777";
        var package = PortableDataPackageService.PrepareExport(
            sourceSettings,
            sourceCatalog,
            new PortablePackageSelection
            {
                TemplateIds = ["template-source"],
                NamedDestinationIds = ["public-destination"],
                BatchPresetIds = ["Shared name"]
            },
            _ => macro.Bytes);

        var targetSettings = CreateTargetSettings();
        targetSettings.NamedDestinations =
        [
            new NamedDestination
            {
                Id = "existing-destination",
                Name = "Shared name",
                Value = "999",
                AccountKeys = []
            }
        ];
        targetSettings.BatchLaunchPresets =
        [
            new BatchLaunchPreset
            {
                Name = "Shared name",
                AccountKeys = [TargetKeyA, TargetKeyB],
                DelaySeconds = 8
            }
        ];
        var targetCatalog = SessionTemplatePolicy.CreateDefault();
        targetCatalog.Templates.Add(new SessionTemplate
        {
            Id = "existing-template",
            Name = "Shared name",
            ClientSlots =
            [
                new SessionTemplateClientSlot
                {
                    AccountKey = TargetKeyA
                }
            ]
        });
        var otherMacro = CreateMacro(
            "Shared name",
            SessionMacroKind.Client,
            TargetKeyA,
            hasKeyboard: true);
        targetCatalog.MacroDefinitions.Add(otherMacro.Definition);

        var plan = PortableDataPackageService.PrepareImport(
            package.ArchiveBytes,
            targetSettings,
            targetCatalog,
            ExactWheelTestData.Display());
        var first = plan.Apply();
        var second = plan.Apply();

        Assert.True(plan.HasChanges);
        Assert.Equal(1, plan.ImportedTemplateCount);
        Assert.Equal(1, plan.ImportedMacroCount);
        Assert.Equal(1, plan.ImportedNamedDestinationCount);
        Assert.Equal(1, plan.ImportedBatchPresetCount);
        Assert.DoesNotContain(
            targetSettings.NamedDestinations,
            item => item.Name.EndsWith(
                "(imported)",
                StringComparison.Ordinal));
        Assert.Single(targetCatalog.Templates);
        Assert.NotSame(first.Settings, second.Settings);
        Assert.NotSame(first.Catalog, second.Catalog);
        Assert.NotSame(first.MacroBlobs[0].Bytes, second.MacroBlobs[0].Bytes);

        var importedDestination = Assert.Single(
            first.Settings.NamedDestinations,
            item => item.Id != "existing-destination");
        Assert.Equal("Shared name (imported)", importedDestination.Name);
        Assert.Equal("12345", importedDestination.Value);
        Assert.Equal([TargetKeyA], importedDestination.AccountKeys);
        var importedPreset = Assert.Single(
            first.Settings.BatchLaunchPresets,
            item => item.Name != "Shared name");
        Assert.Equal("Shared name (imported)", importedPreset.Name);
        Assert.Equal([TargetKeyA, TargetKeyB], importedPreset.AccountKeys);
        var importedTemplate = Assert.Single(
            first.Catalog.Templates,
            item => item.Id != "existing-template");
        Assert.Equal("Shared name (imported)", importedTemplate.Name);
        var slot = Assert.Single(importedTemplate.ClientSlots);
        Assert.Equal(TargetKeyA, slot.AccountKey);
        Assert.Equal("777", slot.Destination);
        Assert.Null(slot.Placement!.MonitorStableId);
        Assert.Null(slot.Placement.MonitorDeviceName);
        Assert.Equal(1, slot.Placement.MonitorIndex);
        var importedDefinition = Assert.Single(
            first.Catalog.MacroDefinitions,
            definition => definition.ContentId == macro.Definition.ContentId);
        Assert.Equal("Shared name (imported)", importedDefinition.Name);
        Assert.Equal(TargetKeyA, importedDefinition.RecordedAccountKey);
        Assert.Equal(macro.Bytes, Assert.Single(first.MacroBlobs).Bytes);
    }

    [Fact]
    public void PrepareImport_MissingAccountSkipsTemplateButKeepsMacro()
    {
        var macro = CreateMacro(
            "Portable macro",
            SessionMacroKind.Client,
            SourceKeyA,
            hasKeyboard: false);
        var package = PortableDataPackageService.PrepareExport(
            CreateSourceSettings(),
            CreateCatalog(macro),
            new PortablePackageSelection
            {
                TemplateIds = ["template-source"]
            },
            _ => macro.Bytes);
        var targetSettings = new AppSettings
        {
            Accounts =
            [
                Account(TargetKeyB, 202, "target-b")
            ]
        };

        var plan = PortableDataPackageService.PrepareImport(
            package.ArchiveBytes,
            targetSettings,
            SessionTemplatePolicy.CreateDefault());
        var applied = plan.Apply();

        Assert.Equal(0, plan.ImportedTemplateCount);
        Assert.Equal(1, plan.SkippedTemplateCount);
        Assert.Equal(2, plan.UnmatchedAccountReferenceCount);
        Assert.Equal(1, plan.ImportedMacroCount);
        Assert.Empty(applied.Catalog.Templates);
        Assert.Single(applied.Catalog.MacroDefinitions);
        Assert.Single(applied.MacroBlobs);
    }

    [Fact]
    public void PrepareImport_WholeLayoutTopologyMismatchUnassignsButPreservesBlob()
    {
        var macro = CreateMacro(
            "Whole layout",
            SessionMacroKind.WholeLayout,
            recordedAccountKey: null,
            hasKeyboard: false,
            display: ExactWheelTestData.Display(
                virtualLeft: 0,
                virtualTop: 0,
                virtualWidth: 1920,
                virtualHeight: 1080));
        var catalog = CreateCatalog(macro);
        var template = catalog.Templates[0];
        template.MacroMode = SessionTemplateMacroMode.WholeLayout;
        template.WholeLayoutMacroId = macro.Definition.ContentId;
        template.ClientSlots[0].PerClientMacroId = null;
        var package = PortableDataPackageService.PrepareExport(
            CreateSourceSettings(),
            catalog,
            new PortablePackageSelection
            {
                TemplateIds = ["template-source"]
            },
            _ => macro.Bytes);
        var incompatible = ExactWheelTestData.Display(
            virtualLeft: 0,
            virtualTop: 0,
            virtualWidth: 1600,
            virtualHeight: 1200);

        var plan = PortableDataPackageService.PrepareImport(
            package.ArchiveBytes,
            CreateTargetSettings(),
            SessionTemplatePolicy.CreateDefault(),
            incompatible);
        var applied = plan.Apply();

        Assert.Equal(1, plan.UnassignedWholeLayoutMacroCount);
        var assignment = Assert.Single(plan.WholeLayoutAssignments);
        Assert.False(assignment.IsAssigned);
        Assert.Contains(
            PortableDeviceAdaptationReason.VirtualAspectRatioMismatch,
            assignment.AdaptationReasons);
        Assert.Equal(1920, assignment.RecordedDisplay.VirtualWidth);
        var importedTemplate = Assert.Single(applied.Catalog.Templates);
        Assert.Equal(SessionTemplateMacroMode.None, importedTemplate.MacroMode);
        Assert.Null(importedTemplate.WholeLayoutMacroId);
        Assert.Single(applied.Catalog.MacroDefinitions);
        Assert.Single(applied.MacroBlobs);
    }

    [Fact]
    public void PrepareImport_IdenticalKindAndHashDeduplicatesDefinition()
    {
        var macro = CreateMacro(
            "Macro",
            SessionMacroKind.Client,
            SourceKeyA,
            hasKeyboard: false);
        var package = PortableDataPackageService.PrepareExport(
            CreateSourceSettings(),
            CreateCatalog(macro),
            new PortablePackageSelection
            {
                MacroContentIds = [macro.Definition.ContentId]
            },
            _ => macro.Bytes);
        var localCatalog = SessionTemplatePolicy.CreateDefault();
        localCatalog.MacroDefinitions.Add(new MacroDefinition
        {
            ContentId = macro.Definition.ContentId,
            SafeFileName = macro.Definition.SafeFileName,
            Name = "Already here",
            Kind = macro.Definition.Kind,
            DurationMilliseconds = macro.Definition.DurationMilliseconds,
            EventCount = macro.Definition.EventCount,
            Sha256 = macro.Definition.Sha256,
            RecordedAtUtc = DateTimeOffset.UtcNow
        });

        var plan = PortableDataPackageService.PrepareImport(
            package.ArchiveBytes,
            CreateTargetSettings(),
            localCatalog);
        var applied = plan.Apply();

        Assert.Equal(0, plan.ImportedMacroCount);
        Assert.Equal(1, plan.DeduplicatedMacroCount);
        Assert.Single(applied.Catalog.MacroDefinitions);
        var blob = Assert.Single(applied.MacroBlobs);
        Assert.False(blob.NeedsCatalogDefinition);
        Assert.Equal(macro.Bytes, blob.Bytes);
    }

    [Theory]
    [InlineData("unknown-entry")]
    [InlineData("traversal-entry")]
    [InlineData("case-collision")]
    [InlineData("unmapped-json")]
    [InlineData("duplicate-json")]
    [InlineData("tampered-macro")]
    [InlineData("unsupported-version")]
    [InlineData("unlisted-macro")]
    [InlineData("missing-macro")]
    [InlineData("hash-mismatch")]
    public void PrepareImport_MalformedOrUnreviewedPackageFailsClosed(
        string mutation)
    {
        var macro = CreateMacro(
            "Macro",
            SessionMacroKind.Client,
            SourceKeyA,
            hasKeyboard: false);
        var package = PortableDataPackageService.PrepareExport(
            CreateSourceSettings(),
            CreateCatalog(macro),
            new PortablePackageSelection
            {
                MacroContentIds = [macro.Definition.ContentId]
            },
            _ => macro.Bytes);

        var mutated = MutateArchive(package.ArchiveBytes, mutation);

        Assert.Throws<InvalidDataException>(() =>
            PortableDataPackageService.PrepareImport(
                mutated,
                CreateTargetSettings(),
                SessionTemplatePolicy.CreateDefault()));
    }

    [Fact]
    public void PrepareExport_AmbiguousRobloxUserIdCannotCollapseTemplateSlots()
    {
        var macro = CreateMacro(
            "Macro",
            SessionMacroKind.Client,
            SourceKeyA,
            hasKeyboard: false);
        var settings = CreateSourceSettings();
        settings.Accounts[1].UserId = settings.Accounts[0].UserId;

        Assert.Throws<InvalidDataException>(() =>
            PortableDataPackageService.PrepareExport(
                settings,
                CreateCatalog(macro),
                new PortablePackageSelection
                {
                    TemplateIds = ["template-source"]
                },
                _ => macro.Bytes));
    }

    [Fact]
    public void PrepareExport_ZeroDurationPlayableMacroIsSupported()
    {
        var macro = CreateMacro(
            "Instant",
            SessionMacroKind.Client,
            SourceKeyA,
            hasKeyboard: false,
            zeroDuration: true);

        var package = PortableDataPackageService.PrepareExport(
            CreateSourceSettings(),
            CreateCatalog(macro),
            new PortablePackageSelection
            {
                MacroContentIds = [macro.Definition.ContentId]
            },
            _ => macro.Bytes);
        var plan = PortableDataPackageService.PrepareImport(
            package.ArchiveBytes,
            CreateTargetSettings(),
            SessionTemplatePolicy.CreateDefault());

        Assert.Equal(1, plan.ImportedMacroCount);
    }

    [Fact]
    public async Task WriteAndReadPackageFile_AtomicallyReplaceReviewedBytes()
    {
        var macro = CreateMacro(
            "Macro",
            SessionMacroKind.Client,
            SourceKeyA,
            hasKeyboard: false);
        var package = PortableDataPackageService.PrepareExport(
            CreateSourceSettings(),
            CreateCatalog(macro),
            new PortablePackageSelection
            {
                MacroContentIds = [macro.Definition.ContentId]
            },
            _ => macro.Bytes);
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "transfer.sessiondock");
        await File.WriteAllTextAsync(
            path,
            "old contents",
            TestContext.Current.CancellationToken);

        await PortableDataPackageService.WritePackageFileAsync(
            path,
            package,
            TestContext.Current.CancellationToken);
        var read = await PortableDataPackageService.ReadPackageFileAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(package.ArchiveBytes, read);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
        var plan = PortableDataPackageService.PrepareImport(
            read,
            CreateTargetSettings(),
            SessionTemplatePolicy.CreateDefault());
        Assert.Equal(1, plan.ImportedMacroCount);
    }

    [Fact]
    public async Task WritePackageFile_PreCancelledOperationPreservesDestination()
    {
        var macro = CreateMacro(
            "Macro",
            SessionMacroKind.Client,
            SourceKeyA,
            hasKeyboard: false);
        var package = PortableDataPackageService.PrepareExport(
            CreateSourceSettings(),
            CreateCatalog(macro),
            new PortablePackageSelection
            {
                MacroContentIds = [macro.Definition.ContentId]
            },
            _ => macro.Bytes);
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "transfer.sessiondock");
        await File.WriteAllTextAsync(
            path,
            "keep me",
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PortableDataPackageService.WritePackageFileAsync(
                path,
                package,
                cancellation.Token));

        Assert.Equal(
            "keep me",
            await File.ReadAllTextAsync(
                path,
                TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    private static AppSettings CreateSourceSettings() => new()
    {
        Accounts =
        [
            Account(SourceKeyA, 101, "source-user-a"),
            Account(SourceKeyB, 202, "source-user-b")
        ]
    };

    private static AppSettings CreateTargetSettings() => new()
    {
        Accounts =
        [
            Account(TargetKeyA, 101, "target-user-a"),
            Account(TargetKeyB, 202, "target-user-b")
        ]
    };

    private static AccountProfile Account(
        string key,
        long userId,
        string username) => new()
        {
            Key = key,
            UserId = userId,
            Username = username,
            SessionFolder = $@"Profiles\{key}"
        };

    private static SessionTemplateCatalog CreateCatalog(MacroFixture macro)
    {
        var catalog = SessionTemplatePolicy.CreateDefault();
        catalog.MacroDefinitions.Add(macro.Definition);
        catalog.Templates.Add(new SessionTemplate
        {
            Id = "template-source",
            Name = "Portable template",
            DelaySeconds = 5,
            LayoutMode = SessionTemplateLayoutMode.Saved,
            MacroMode = SessionTemplateMacroMode.PerClient,
            ClientSlots =
            [
                new SessionTemplateClientSlot
                {
                    SlotId = "slot-source",
                    AccountKey = SourceKeyA,
                    Order = 0,
                    Placement = new NormalizedClientWindowPlacement
                    {
                        MonitorStableId = "MONITOR-SERIAL-CANARY",
                        MonitorDeviceName = @"\\.\DISPLAY9",
                        MonitorIndex = 1,
                        Left = 0.1,
                        Top = 0.2,
                        Width = 0.6,
                        Height = 0.7
                    },
                    PerClientMacroId = macro.Definition.ContentId
                }
            ],
            UpdatedAtUtc = new DateTimeOffset(
                2026,
                8,
                4,
                9,
                0,
                0,
                TimeSpan.Zero)
        });
        return catalog;
    }

    private static MacroFixture CreateMacro(
        string name,
        SessionMacroKind kind,
        string? recordedAccountKey,
        bool hasKeyboard,
        ExactWheelDisplayTopology? display = null,
        bool zeroDuration = false)
    {
        ExactWheelInputEvent[] events = hasKeyboard
            ? ExactWheelTestData.Events()
            :
            [
                new ExactWheelInputEvent(
                    0,
                    1,
                    ExactWheelInputEventType.MouseMove,
                    100,
                    80,
                    0,
                    0)
            ];
        var duration = zeroDuration ? 0UL : hasKeyboard ? 500_000UL : 1UL;
        var recording = ExactWheelTestData.Recording(
            events,
            duration,
            display);
        var bytes = ExactWheelMacroSerializer.Serialize(recording);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var lower = sha256.ToLowerInvariant();
        return new MacroFixture(
            new MacroDefinition
            {
                ContentId = kind == SessionMacroKind.Client
                    ? "ew-client-" + lower
                    : "ew-whole-layout-" + lower,
                SafeFileName = lower + ".ewmacro",
                Name = name,
                Kind = kind,
                RecordedAccountKey = recordedAccountKey,
                DurationMilliseconds = checked((long)(
                    (duration + 999UL) / 1_000UL)),
                EventCount = events.Length,
                Sha256 = sha256,
                RecordedAtUtc = DateTimeOffset.UtcNow
            },
            bytes,
            lower);
    }

    private static Dictionary<string, byte[]> ReadArchive(byte[] contents)
    {
        using var input = new MemoryStream(contents, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var stream = entry.Open();
                using var output = new MemoryStream();
                stream.CopyTo(output);
                return output.ToArray();
            },
            StringComparer.Ordinal);
    }

    private static byte[] MutateArchive(byte[] source, string mutation)
    {
        var entries = ReadArchive(source).ToList();
        var manifestIndex = entries.FindIndex(item =>
            item.Key == PortableDataPackageService.ManifestEntryName);
        var macroIndex = entries.FindIndex(item =>
            item.Key.StartsWith("macros/", StringComparison.Ordinal));
        switch (mutation)
        {
            case "unknown-entry":
                entries.Add(new("notes.txt", [1]));
                break;
            case "traversal-entry":
                entries.Add(new("../escape.txt", [1]));
                break;
            case "case-collision":
                entries.Add(new("MANIFEST.JSON", [1]));
                break;
            case "unmapped-json":
                {
                    var json = JsonNode.Parse(entries[manifestIndex].Value)!
                        .AsObject();
                    json["unexpected"] = true;
                    entries[manifestIndex] = new(
                        entries[manifestIndex].Key,
                        Encoding.UTF8.GetBytes(json.ToJsonString()));
                    break;
                }
            case "duplicate-json":
                {
                    var text = Encoding.UTF8.GetString(
                        entries[manifestIndex].Value);
                    text = text.Replace(
                        "\"version\": 1,",
                        "\"version\": 1, \"version\": 1,",
                        StringComparison.Ordinal);
                    entries[manifestIndex] = new(
                        entries[manifestIndex].Key,
                        Encoding.UTF8.GetBytes(text));
                    break;
                }
            case "tampered-macro":
                entries[macroIndex].Value[^1] ^= 1;
                break;
            case "unsupported-version":
                {
                    var json = JsonNode.Parse(entries[manifestIndex].Value)!
                        .AsObject();
                    json["version"] = 2;
                    entries[manifestIndex] = new(
                        entries[manifestIndex].Key,
                        Encoding.UTF8.GetBytes(json.ToJsonString()));
                    break;
                }
            case "unlisted-macro":
                entries.Add(new(
                    $"macros/{new string('c', 64)}.ewmacro",
                    [1]));
                break;
            case "missing-macro":
                entries.RemoveAt(macroIndex);
                break;
            case "hash-mismatch":
                {
                    var json = JsonNode.Parse(entries[manifestIndex].Value)!
                        .AsObject();
                    json["macros"]![0]!["sha256"] = new string('c', 64);
                    entries[manifestIndex] = new(
                        entries[manifestIndex].Key,
                        Encoding.UTF8.GetBytes(json.ToJsonString()));
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(
                   output,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Key);
                using var stream = entry.Open();
                stream.Write(item.Value);
            }
        }
        return output.ToArray();
    }

    private sealed record MacroFixture(
        MacroDefinition Definition,
        byte[] Bytes,
        string Sha256Lower);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SessionDock.Portable.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Test cleanup is best effort on Windows.
            }
        }
    }
}
