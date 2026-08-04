using System.Text.Json.Nodes;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SessionTemplateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.Templates.{Guid.NewGuid():N}");

    [Fact]
    public void Read_MissingStoreReturnsDefaultWithoutCreatingDirectories()
    {
        var store = new SessionTemplateStore(_root);

        var result = store.Read();

        Assert.False(result.Exists);
        Assert.True(result.IsValid);
        Assert.False(result.RecoveredFromBackup);
        Assert.False(result.WasNormalized);
        Assert.Empty(result.Catalog.Templates);
        Assert.Empty(result.Catalog.MacroDefinitions);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void WriteThenRead_RoundTripsNormalizedCatalogAndCreatesMacroRoot()
    {
        var store = new SessionTemplateStore(_root);
        var source = CreateCatalog("template-one", "macro-one");
        source.Templates[0].Name = "  Morning   run ";
        source.Templates[0].ClientSlots[0].Destination = "  12345  ";
        source.MacroDefinitions[0].Sha256 = new string('b', 64);

        store.Write(source);
        var result = store.Read();

        Assert.True(result.Exists);
        Assert.True(result.IsValid);
        Assert.False(result.RecoveredFromBackup);
        Assert.False(result.WasNormalized);
        Assert.Equal("Morning run", Assert.Single(result.Catalog.Templates).Name);
        Assert.Equal(
            new string('B', 64),
            Assert.Single(result.Catalog.MacroDefinitions).Sha256);
        Assert.Equal(
            "monitor-interface-primary",
            result.Catalog.Templates[0].ClientSlots[0].Placement!.MonitorStableId);
        Assert.Equal(
            "12345",
            result.Catalog.Templates[0].ClientSlots[0].Destination);
        Assert.True(Directory.Exists(store.MacrosDirectory));
        Assert.EndsWith(
            "\n",
            File.ReadAllText(CatalogPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Read_CorruptPrimaryRecoversPreviousAtomicBackup()
    {
        var store = new SessionTemplateStore(_root);
        store.Write(CreateCatalog("first-template", "first-macro"));
        store.Write(CreateCatalog("second-template", "second-macro"));
        File.WriteAllText(CatalogPath, "{not-json");

        var result = store.Read();

        Assert.True(result.Exists);
        Assert.True(result.IsValid);
        Assert.True(result.RecoveredFromBackup);
        Assert.Equal(
            "first-template",
            Assert.Single(result.Catalog.Templates).Id);
        Assert.Equal(
            "first-macro",
            Assert.Single(result.Catalog.MacroDefinitions).ContentId);
    }

    [Fact]
    public void Read_InvalidUtf8DuplicatePropertiesAndOversizeFailClosed()
    {
        Directory.CreateDirectory(TemplatesDirectory);
        var store = new SessionTemplateStore(_root);
        File.WriteAllBytes(CatalogPath, [0xc3, 0x28]);

        AssertInvalid(store.Read());

        File.WriteAllText(
            CatalogPath,
            """{"schemaVersion":1,"schemaVersion":1}""");
        AssertInvalid(store.Read());

        File.WriteAllBytes(
            CatalogPath,
            new byte[SessionTemplateStore.MaximumCatalogBytes + 1]);
        AssertInvalid(store.Read());
    }

    [Fact]
    public void ReadAndWrite_RejectInjectedReparseDirectories()
    {
        Directory.CreateDirectory(TemplatesDirectory);
        var redirectedStore = new SessionTemplateStore(
            _root,
            path =>
            {
                var attributes = File.GetAttributes(path);
                return Path.GetFullPath(path).Equals(
                    Path.GetFullPath(TemplatesDirectory),
                    StringComparison.OrdinalIgnoreCase)
                    ? attributes | FileAttributes.ReparsePoint
                    : attributes;
            });

        var result = redirectedStore.Read();

        Assert.False(result.IsValid);
        Assert.Throws<IOException>(() =>
            redirectedStore.Write(
                CreateCatalog("template-one", "macro-one")));
    }

    [Fact]
    public void RoundTrip_PreservesStaleAccountAndMacroReferencesForRepair()
    {
        var store = new SessionTemplateStore(_root);
        var catalog = CreateCatalog("template-one", "unused-macro");
        catalog.Templates[0].MacroMode =
            SessionTemplateMacroMode.PerClient;
        catalog.Templates[0].ClientSlots[0].AccountKey = "deleted-account";
        catalog.Templates[0].ClientSlots[0].PerClientMacroId =
            "deleted-macro";

        store.Write(catalog);
        var result = store.Read();

        var slot = Assert.Single(
            Assert.Single(result.Catalog.Templates).ClientSlots);
        Assert.Equal("deleted-account", slot.AccountKey);
        Assert.Equal("deleted-macro", slot.PerClientMacroId);
        Assert.DoesNotContain(
            result.Catalog.MacroDefinitions,
            macro => macro.ContentId == slot.PerClientMacroId);
    }

    [Fact]
    public void RoundTrip_PreservesContinuousWholeLayoutPlaybackChoice()
    {
        var store = new SessionTemplateStore(_root);
        var catalog = CreateCatalog("whole-template", "whole-macro");
        var template = catalog.Templates[0];
        template.MacroMode = SessionTemplateMacroMode.WholeLayout;
        template.SharedMacroId = null;
        template.WholeLayoutMacroId = "whole-macro";
        template.RepeatWholeLayoutMacro = true;
        catalog.MacroDefinitions[0].Kind = SessionMacroKind.WholeLayout;
        catalog.MacroDefinitions[0].RecordedAccountKey = null;

        store.Write(catalog);
        var result = store.Read();

        var restored = Assert.Single(result.Catalog.Templates);
        Assert.Equal(
            SessionTemplateMacroMode.WholeLayout,
            restored.MacroMode);
        Assert.Equal("whole-macro", restored.WholeLayoutMacroId);
        Assert.True(restored.RepeatWholeLayoutMacro);
    }

    [Fact]
    public void RoundTrip_PreservesExplicitSharedMacroTargets()
    {
        var store = new SessionTemplateStore(_root);
        var catalog = CreateCatalog("shared-template", "shared-macro");
        var template = catalog.Templates[0];
        template.ClientSlots.Add(new SessionTemplateClientSlot
        {
            SlotId = "shared-template-second-slot",
            AccountKey = "second-account",
            Order = 1
        });
        template.SharedMacroAccountKeys = ["second-account"];

        store.Write(catalog);
        var result = store.Read();

        var restored = Assert.Single(result.Catalog.Templates);
        Assert.Equal(["second-account"], restored.SharedMacroAccountKeys);
        Assert.Equal(
            "second-account",
            Assert.Single(
                SessionTemplatePolicy.SelectSharedMacroTargetSlots(restored))
                .AccountKey);
    }

    [Fact]
    public void Read_LegacySharedTemplateWithoutTargetFieldTargetsAllClients()
    {
        var store = new SessionTemplateStore(_root);
        var catalog = CreateCatalog("legacy-shared", "shared-macro");
        catalog.Templates[0].ClientSlots.Add(new SessionTemplateClientSlot
        {
            SlotId = "legacy-shared-second-slot",
            AccountKey = "second-account",
            Order = 1
        });
        store.Write(catalog);
        var document = JsonNode.Parse(File.ReadAllText(CatalogPath))!;
        var template = document["templates"]![0]!.AsObject();
        Assert.True(template.Remove("sharedMacroAccountKeys"));
        File.WriteAllText(CatalogPath, document.ToJsonString());

        var result = store.Read();

        var restored = Assert.Single(result.Catalog.Templates);
        Assert.Null(restored.SharedMacroAccountKeys);
        Assert.Equal(
            ["saved-account", "second-account"],
            SessionTemplatePolicy.SelectSharedMacroTargetSlots(restored)
                .Select(slot => slot.AccountKey));
    }

    [Fact]
    public void Read_LegacyCatalogMigratesSpeedAndWritesExplicitMacroKind()
    {
        var store = new SessionTemplateStore(_root);
        store.Write(CreateCatalog("legacy-template", "legacy-macro"));
        RewriteCatalog(root =>
        {
            MakeLegacyCatalog(root);
            Assert.True(root["macroDefinitions"]![0]!
                .AsObject()
                .Remove("kind"));
        });

        var result = store.Read();

        Assert.True(result.IsValid);
        Assert.True(result.WasNormalized);
        Assert.Equal(
            SessionTemplatePolicy.CatalogSchemaVersion,
            result.Catalog.SchemaVersion);
        Assert.Equal(1.0, result.Catalog.TemplatePreferences.MacroPlaybackSpeed);
        Assert.Equal(
            MacroRecordingHotkeyPolicy.DefaultValue,
            result.Catalog.TemplatePreferences.MacroRecordingStopHotkey);
        Assert.Equal(
            SessionMacroKind.Client,
            Assert.Single(result.Catalog.MacroDefinitions).Kind);

        store.Write(result.Catalog);
        var rewritten = JsonNode.Parse(File.ReadAllText(CatalogPath))!;
        Assert.Equal(
            SessionTemplatePolicy.CatalogSchemaVersion,
            rewritten["schemaVersion"]!.GetValue<int>());
        Assert.Equal(
            1.0,
            rewritten["templatePreferences"]!["macroPlaybackSpeed"]!
                .GetValue<double>());
        Assert.Equal(
            MacroRecordingHotkeyPolicy.DefaultValue,
            rewritten["templatePreferences"]!["macroRecordingStopHotkey"]!
                .GetValue<string>());
        Assert.Equal(
            "client",
            rewritten["macroDefinitions"]![0]!["kind"]!
                .GetValue<string>());
    }

    [Fact]
    public void Read_SchemaTwoCatalogMigratesDefaultRecordingStopHotkey()
    {
        var store = new SessionTemplateStore(_root);
        store.Write(CreateCatalog("schema-two-template", "schema-two-macro"));
        RewriteCatalog(root =>
        {
            root["schemaVersion"] =
                SessionTemplatePolicy.PreviousCatalogSchemaVersion;
            Assert.True(root["templatePreferences"]!
                .AsObject()
                .Remove("macroRecordingStopHotkey"));
        });

        var result = store.Read();

        Assert.True(result.IsValid);
        Assert.True(result.WasNormalized);
        Assert.Equal(
            SessionTemplatePolicy.CatalogSchemaVersion,
            result.Catalog.SchemaVersion);
        Assert.Equal(
            MacroRecordingHotkeyPolicy.DefaultValue,
            result.Catalog.TemplatePreferences.MacroRecordingStopHotkey);
    }

    [Fact]
    public void Read_LegacyMissingKindInfersWholeLayoutFromTemplateReference()
    {
        var store = new SessionTemplateStore(_root);
        var catalog = CreateCatalog("whole-template", "legacy-whole");
        var template = catalog.Templates[0];
        template.MacroMode = SessionTemplateMacroMode.WholeLayout;
        template.SharedMacroId = null;
        template.WholeLayoutMacroId = "legacy-whole";
        catalog.MacroDefinitions[0].Kind = SessionMacroKind.WholeLayout;
        store.Write(catalog);
        RewriteCatalog(root =>
        {
            MakeLegacyCatalog(root);
            Assert.True(root["macroDefinitions"]![0]!
                .AsObject()
                .Remove("kind"));
        });

        var result = store.Read();

        Assert.True(result.IsValid);
        Assert.Equal(
            SessionMacroKind.WholeLayout,
            Assert.Single(result.Catalog.MacroDefinitions).Kind);
    }

    [Fact]
    public void Read_LegacyMissingKindUsesKindSpecificIdOverConflictingReference()
    {
        var store = new SessionTemplateStore(_root);
        var sha256 = new string('A', 64);
        var macroId = "ew-whole-layout-" + sha256.ToLowerInvariant();
        var catalog = CreateCatalog("shared-template", macroId);
        catalog.MacroDefinitions[0].Sha256 = sha256;
        catalog.MacroDefinitions[0].Kind = SessionMacroKind.WholeLayout;
        store.Write(catalog);
        RewriteCatalog(root =>
        {
            MakeLegacyCatalog(root);
            Assert.True(root["macroDefinitions"]![0]!
                .AsObject()
                .Remove("kind"));
        });

        var result = store.Read();

        Assert.True(result.IsValid);
        Assert.Equal(
            SessionMacroKind.WholeLayout,
            Assert.Single(result.Catalog.MacroDefinitions).Kind);
    }

    [Fact]
    public void Read_LegacyExplicitKindIsNeverReinferredFromTemplateUsage()
    {
        var store = new SessionTemplateStore(_root);
        var catalog = CreateCatalog("shared-template", "explicit-whole");
        catalog.MacroDefinitions[0].Kind = SessionMacroKind.WholeLayout;
        store.Write(catalog);
        RewriteCatalog(MakeLegacyCatalog);

        var result = store.Read();

        Assert.True(result.IsValid);
        Assert.Equal(
            SessionMacroKind.WholeLayout,
            Assert.Single(result.Catalog.MacroDefinitions).Kind);
    }

    [Fact]
    public void Read_LegacyMissingKindDefaultsClientWhenReferencesConflict()
    {
        var store = new SessionTemplateStore(_root);
        var catalog = CreateCatalog("shared-template", "legacy-ambiguous");
        var whole = CreateCatalog("whole-template", "unused")
            .Templates[0];
        whole.MacroMode = SessionTemplateMacroMode.WholeLayout;
        whole.SharedMacroId = null;
        whole.WholeLayoutMacroId = "legacy-ambiguous";
        catalog.Templates.Add(whole);
        store.Write(catalog);
        RewriteCatalog(root =>
        {
            MakeLegacyCatalog(root);
            Assert.True(root["macroDefinitions"]![0]!
                .AsObject()
                .Remove("kind"));
        });

        var result = store.Read();

        Assert.True(result.IsValid);
        Assert.Equal(
            SessionMacroKind.Client,
            Assert.Single(result.Catalog.MacroDefinitions).Kind);
    }

    [Fact]
    public void Read_RejectsSchemaOneWithNewPreferenceShape()
    {
        var store = new SessionTemplateStore(_root);
        store.Write(CreateCatalog("template-one", "macro-one"));
        RewriteCatalog(root => root["schemaVersion"] =
            SessionTemplatePolicy.LegacyCatalogSchemaVersion);

        AssertInvalid(store.Read());
    }

    [Fact]
    public void Read_RejectsSchemaTwoMacroWithoutIntrinsicKind()
    {
        var store = new SessionTemplateStore(_root);
        store.Write(CreateCatalog("template-one", "macro-one"));
        RewriteCatalog(root => Assert.True(
            root["macroDefinitions"]![0]!.AsObject().Remove("kind")));

        AssertInvalid(store.Read());
    }

    [Fact]
    public void Read_RejectsSchemaThreeWithoutRecordingStopHotkey()
    {
        var store = new SessionTemplateStore(_root);
        store.Write(CreateCatalog("template-one", "macro-one"));
        RewriteCatalog(root => Assert.True(root["templatePreferences"]!
            .AsObject()
            .Remove("macroRecordingStopHotkey")));

        AssertInvalid(store.Read());
    }

    [Fact]
    public void Read_RejectsPresentButInvalidStableMonitorIdentity()
    {
        var store = new SessionTemplateStore(_root);
        store.Write(CreateCatalog("template-one", "macro-one"));
        RewriteCatalog(root =>
            root["templates"]![0]!["clientSlots"]![0]!["placement"]![
                "monitorStableId"] = null);

        AssertInvalid(store.Read());
    }

    [Fact]
    public void Read_RejectsFutureCatalogSchema()
    {
        var store = new SessionTemplateStore(_root);
        store.Write(CreateCatalog("template-one", "macro-one"));
        RewriteCatalog(root => root["schemaVersion"] =
            SessionTemplatePolicy.CatalogSchemaVersion + 1);

        AssertInvalid(store.Read());
    }

    [Fact]
    public void Write_NeverDeletesUnreferencedMacroFiles()
    {
        var store = new SessionTemplateStore(_root);
        store.Write(CreateCatalog("template-one", "macro-one"));
        var orphanPath = Path.Combine(
            store.MacrosDirectory,
            "unreferenced.macro");
        File.WriteAllText(orphanPath, "local macro contents");

        store.Write(new SessionTemplateCatalog());

        Assert.True(File.Exists(orphanPath));
        Assert.Equal(
            "local macro contents",
            File.ReadAllText(orphanPath));
    }

    [Fact]
    public void Write_CorruptExistingCatalogRequiresExplicitRepair()
    {
        Directory.CreateDirectory(TemplatesDirectory);
        File.WriteAllText(CatalogPath, "{not-json");
        var store = new SessionTemplateStore(_root);
        var replacement = CreateCatalog("replacement", "macro-one");

        Assert.Throws<InvalidDataException>(() => store.Write(replacement));
        store.Write(replacement, repairInvalidCatalog: true);

        var repaired = store.Read();
        Assert.True(repaired.IsValid);
        Assert.False(repaired.RecoveredFromBackup);
        Assert.Equal(
            "replacement",
            Assert.Single(repaired.Catalog.Templates).Id);
    }

    private string TemplatesDirectory => Path.Combine(_root, "Templates");

    private string CatalogPath => Path.Combine(
        TemplatesDirectory,
        SessionTemplateStore.CatalogFileName);

    private void RewriteCatalog(Action<JsonObject> rewrite)
    {
        var root = JsonNode.Parse(File.ReadAllText(CatalogPath))!.AsObject();
        rewrite(root);
        File.WriteAllText(CatalogPath, root.ToJsonString());
    }

    private static void MakeLegacyCatalog(JsonObject root)
    {
        root["schemaVersion"] =
            SessionTemplatePolicy.LegacyCatalogSchemaVersion;
        Assert.True(root["templatePreferences"]!
            .AsObject()
            .Remove("macroPlaybackSpeed"));
        Assert.True(root["templatePreferences"]!
            .AsObject()
            .Remove("macroRecordingStopHotkey"));
    }

    private static void AssertInvalid(SessionTemplateCatalogReadResult result)
    {
        Assert.True(result.Exists);
        Assert.False(result.IsValid);
        Assert.False(result.RecoveredFromBackup);
        Assert.Empty(result.Catalog.Templates);
        Assert.Empty(result.Catalog.MacroDefinitions);
    }

    private static SessionTemplateCatalog CreateCatalog(
        string templateId,
        string macroId) => new()
        {
            Templates =
        [
            new SessionTemplate
            {
                Id = templateId,
                Name = templateId,
                LayoutMode = SessionTemplateLayoutMode.Saved,
                MacroMode = SessionTemplateMacroMode.Shared,
                SharedMacroId = macroId,
                UpdatedAtUtc = new DateTimeOffset(
                    2026,
                    8,
                    3,
                    10,
                    0,
                    0,
                    TimeSpan.Zero),
                ClientSlots =
                [
                    new SessionTemplateClientSlot
                    {
                        SlotId = $"{templateId}-slot",
                        AccountKey = "saved-account",
                        Placement = new NormalizedClientWindowPlacement
                        {
                            MonitorStableId = "monitor-interface-primary",
                            MonitorDeviceName = @"\\.\DISPLAY1",
                            MonitorIndex = 0,
                            Left = 0.1,
                            Top = 0.1,
                            Width = 0.5,
                            Height = 0.5
                        }
                    }
                ]
            }
        ],
            MacroDefinitions =
        [
            new MacroDefinition
            {
                ContentId = macroId,
                SafeFileName = $"{macroId}.macro",
                Name = macroId,
                Kind = SessionMacroKind.Client,
                RecordedAccountKey = "saved-account",
                DurationMilliseconds = 1000,
                EventCount = 10,
                Sha256 = new string('A', 64),
                RecordedAtUtc = new DateTimeOffset(
                    2026,
                    8,
                    3,
                    10,
                    0,
                    0,
                    TimeSpan.Zero)
            }
        ]
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
