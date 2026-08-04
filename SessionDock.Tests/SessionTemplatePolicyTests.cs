using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SessionTemplatePolicyTests
{
    [Fact]
    public void MacroMetadataEventLimit_TracksExactWheelLimit()
    {
        Assert.Equal(
            checked((int)ExactWheelLimits.MaximumEventCount),
            SessionTemplatePolicy.MaximumEventCount);
    }

    [Fact]
    public void Normalize_BoundsMetadataAndPreservesRepairableReferences()
    {
        var source = new SessionTemplateCatalog
        {
            TemplatePreferences = new TemplatePreferences
            {
                AutoArrangeNormalBatch = false,
                TargetWidth = double.NaN,
                TargetHeight = 100,
                MinimumWidth = 900,
                MinimumHeight = 500,
                RevealX = 500,
                RevealY = double.PositiveInfinity,
                MacroPlaybackSpeed = double.NaN,
                MacroRecordingStopHotkey = "Ctrl+F3",
                PreferredMonitorDeviceName = "  \\\\.\\DISPLAY2  "
            },
            MacroDefinitions =
            [
                CreateMacro("ambiguous-macro", "ambiguous.macro"),
                CreateMacro("ambiguous-macro", "duplicate.macro"),
                CreateMacro("bounded-macro", "bounded.macro")
            ],
            Templates =
            [
                new SessionTemplate
                {
                    Id = " template-one ",
                    Name = "  Farming\t group  ",
                    DelaySeconds = 999,
                    LayoutMode = SessionTemplateLayoutMode.Saved,
                    MacroMode = SessionTemplateMacroMode.PerClient,
                    SharedMacroId = "discarded-shared",
                    WholeLayoutMacroId = "discarded-whole",
                    RepeatWholeLayoutMacro = true,
                    UpdatedAtUtc = new DateTimeOffset(
                        2026,
                        8,
                        3,
                        12,
                        0,
                        0,
                        TimeSpan.FromHours(2)),
                    ClientSlots =
                    [
                        new SessionTemplateClientSlot
                        {
                            SlotId = "later-slot",
                            AccountKey = "missing-account",
                            Order = 8,
                            Destination = "  12345  ",
                            PerClientMacroId = "missing-macro",
                            Placement = new NormalizedClientWindowPlacement
                            {
                                MonitorStableId = "  monitor-interface-primary  ",
                                MonitorDeviceName = "  \\\\.\\DISPLAY2 ",
                                MonitorIndex = 99,
                                Left = 0.9,
                                Top = -1,
                                Width = 0.5,
                                Height = 0.6
                            }
                        },
                        new SessionTemplateClientSlot
                        {
                            SlotId = "first-slot",
                            AccountKey = "saved-account",
                            Order = -4
                        },
                        new SessionTemplateClientSlot
                        {
                            SlotId = "first-slot",
                            AccountKey = "duplicate-is-ignored",
                            Order = 12
                        },
                        new SessionTemplateClientSlot
                        {
                            SlotId = "duplicate-account-slot",
                            AccountKey = "saved-account",
                            Order = 13
                        }
                    ]
                }
            ]
        };
        source.MacroDefinitions[2].DurationMilliseconds = long.MaxValue;
        source.MacroDefinitions[2].EventCount = int.MaxValue;

        var normalized = SessionTemplatePolicy.Normalize(source);

        Assert.False(normalized.TemplatePreferences.AutoArrangeNormalBatch);
        Assert.Equal(900, normalized.TemplatePreferences.TargetWidth);
        Assert.Equal(500, normalized.TemplatePreferences.TargetHeight);
        Assert.Equal(900, normalized.TemplatePreferences.MinimumWidth);
        Assert.Equal(500, normalized.TemplatePreferences.MinimumHeight);
        Assert.Equal(256, normalized.TemplatePreferences.RevealX);
        Assert.Equal(36, normalized.TemplatePreferences.RevealY);
        Assert.Equal(1.0, normalized.TemplatePreferences.MacroPlaybackSpeed);
        Assert.Equal(
            MacroRecordingHotkeyPolicy.DefaultValue,
            normalized.TemplatePreferences.MacroRecordingStopHotkey);
        Assert.Equal(
            @"\\.\DISPLAY2",
            normalized.TemplatePreferences.PreferredMonitorDeviceName);

        var definition = Assert.Single(normalized.MacroDefinitions);
        Assert.Equal("bounded-macro", definition.ContentId);
        Assert.Equal(
            new string('A', 64),
            definition.Sha256);
        Assert.Equal(
            SessionTemplatePolicy.MaximumDurationMilliseconds,
            definition.DurationMilliseconds);
        Assert.Equal(
            SessionTemplatePolicy.MaximumEventCount,
            definition.EventCount);
        Assert.DoesNotContain(
            normalized.MacroDefinitions,
            macro => macro.ContentId == "ambiguous-macro");

        var template = Assert.Single(normalized.Templates);
        Assert.Equal("template-one", template.Id);
        Assert.Equal("Farming group", template.Name);
        Assert.Equal(8, template.DelaySeconds);
        Assert.Null(template.SharedMacroId);
        Assert.Null(template.WholeLayoutMacroId);
        Assert.False(template.RepeatWholeLayoutMacro);
        Assert.Equal(TimeSpan.Zero, template.UpdatedAtUtc.Offset);
        Assert.Equal(2, template.ClientSlots.Count);

        Assert.Equal("first-slot", template.ClientSlots[0].SlotId);
        Assert.Equal(0, template.ClientSlots[0].Order);
        var stale = template.ClientSlots[1];
        Assert.Equal("missing-account", stale.AccountKey);
        Assert.Equal("missing-macro", stale.PerClientMacroId);
        Assert.Equal("12345", stale.Destination);
        Assert.Equal(1, stale.Order);
        Assert.NotNull(stale.Placement);
        Assert.Equal(
            "monitor-interface-primary",
            stale.Placement.MonitorStableId);
        Assert.Equal(0.5, stale.Placement.Left);
        Assert.Equal(0, stale.Placement.Top);
        Assert.Equal(0.5, stale.Placement.Width);
        Assert.Equal(0.6, stale.Placement.Height);
        Assert.Equal(
            SessionTemplatePolicy.MaximumMonitorIndex,
            stale.Placement.MonitorIndex);

        Assert.DoesNotContain(
            normalized.MacroDefinitions,
            macro => macro.ContentId == stale.PerClientMacroId);
    }

    [Fact]
    public void Normalize_KeepsStaleSharedAndWholeLayoutMacroReferences()
    {
        var catalog = new SessionTemplateCatalog
        {
            Templates =
            [
                CreateTemplate(
                    "shared-template",
                    SessionTemplateMacroMode.Shared,
                    sharedMacroId: "missing-shared"),
                CreateTemplate(
                    "whole-template",
                    SessionTemplateMacroMode.WholeLayout,
                    wholeMacroId: "missing-whole",
                    repeatWholeLayoutMacro: true)
            ]
        };

        var normalized = SessionTemplatePolicy.Normalize(catalog);

        Assert.Equal(
            "missing-shared",
            normalized.Templates[0].SharedMacroId);
        Assert.Equal(
            "missing-whole",
            normalized.Templates[1].WholeLayoutMacroId);
        Assert.True(normalized.Templates[1].RepeatWholeLayoutMacro);
        Assert.Empty(normalized.MacroDefinitions);
    }

    [Fact]
    public void Normalize_SharedMacroTargetsAreOrderedFilteredAndFailClosed()
    {
        var template = CreateTemplate(
            "shared-template",
            SessionTemplateMacroMode.Shared,
            sharedMacroId: "shared-macro");
        template.ClientSlots.Add(new SessionTemplateClientSlot
        {
            SlotId = "shared-template-second-slot",
            AccountKey = "second-account",
            Order = 1
        });
        template.SharedMacroAccountKeys =
        [
            " SECOND-account ",
            "missing-account",
            "saved-account",
            "saved-account",
            "not valid!"
        ];

        var normalized = SessionTemplatePolicy.Normalize(
            new SessionTemplateCatalog { Templates = [template] });
        var restored = Assert.Single(normalized.Templates);

        Assert.Equal(
            ["saved-account", "second-account"],
            restored.SharedMacroAccountKeys);
        Assert.Equal(
            ["saved-account", "second-account"],
            SessionTemplatePolicy.SelectSharedMacroTargetSlots(restored)
                .Select(slot => slot.AccountKey));

        restored.SharedMacroAccountKeys = [];
        Assert.Empty(
            SessionTemplatePolicy.SelectSharedMacroTargetSlots(restored));
    }

    [Fact]
    public void SelectSharedMacroTargetSlots_MissingLegacyFieldMeansAllClients()
    {
        var template = CreateTemplate(
            "legacy-shared",
            SessionTemplateMacroMode.Shared,
            sharedMacroId: "shared-macro");
        template.ClientSlots.Add(new SessionTemplateClientSlot
        {
            SlotId = "legacy-shared-second-slot",
            AccountKey = "second-account",
            Order = 1
        });

        var normalized = SessionTemplatePolicy.Normalize(
            new SessionTemplateCatalog { Templates = [template] });
        var restored = Assert.Single(normalized.Templates);

        Assert.Null(restored.SharedMacroAccountKeys);
        Assert.Equal(
            ["saved-account", "second-account"],
            SessionTemplatePolicy.SelectSharedMacroTargetSlots(restored)
                .Select(slot => slot.AccountKey));
    }

    [Fact]
    public void Normalize_DiscardsSharedTargetsOutsideSharedMode()
    {
        var template = CreateTemplate("not-shared");
        template.SharedMacroAccountKeys = ["saved-account"];

        var normalized = SessionTemplatePolicy.Normalize(
            new SessionTemplateCatalog { Templates = [template] });

        Assert.Null(Assert.Single(normalized.Templates)
            .SharedMacroAccountKeys);
    }

    [Fact]
    public void Normalize_ProducesIndependentEquivalentSnapshot()
    {
        var source = new SessionTemplateCatalog
        {
            Templates = [CreateTemplate("template-one")],
            MacroDefinitions =
            [
                CreateMacro("client-macro", "client.macro")
            ]
        };

        var first = SessionTemplatePolicy.Normalize(source);
        var second = SessionTemplatePolicy.Normalize(first);

        Assert.True(SessionTemplatePolicy.AreEquivalent(first, second));
        Assert.NotSame(first, second);
        Assert.NotSame(first.Templates, second.Templates);
        Assert.NotSame(first.Templates[0], second.Templates[0]);
        Assert.NotSame(
            first.Templates[0].ClientSlots[0],
            second.Templates[0].ClientSlots[0]);
        Assert.NotSame(
            first.MacroDefinitions[0],
            second.MacroDefinitions[0]);

        second.Templates[0].Name = "Changed";
        Assert.False(SessionTemplatePolicy.AreEquivalent(first, second));

        second = SessionTemplatePolicy.Normalize(first);
        second.TemplatePreferences.MacroPlaybackSpeed = 2;
        Assert.False(SessionTemplatePolicy.AreEquivalent(first, second));

        second = SessionTemplatePolicy.Normalize(first);
        second.TemplatePreferences.MacroRecordingStopHotkey = "F12";
        Assert.False(SessionTemplatePolicy.AreEquivalent(first, second));
    }

    [Theory]
    [InlineData(0.01, 0.1)]
    [InlineData(0.1, 0.1)]
    [InlineData(2.5, 2)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    [InlineData(100, 2)]
    [InlineData(1000, 2)]
    public void Normalize_ClampsGlobalMacroPlaybackSpeed(
        double sourceSpeed,
        double expectedSpeed)
    {
        var source = new SessionTemplateCatalog
        {
            TemplatePreferences = new TemplatePreferences
            {
                MacroPlaybackSpeed = sourceSpeed
            }
        };

        var normalized = SessionTemplatePolicy.Normalize(source);

        Assert.Equal(
            SessionTemplatePolicy.CatalogSchemaVersion,
            normalized.SchemaVersion);
        Assert.Equal(
            expectedSpeed,
            normalized.TemplatePreferences.MacroPlaybackSpeed);
    }

    [Fact]
    public void Normalize_MigratesLegacyCatalogSchemaAndDefaultsPlaybackSpeed()
    {
        var source = new SessionTemplateCatalog
        {
            SchemaVersion = SessionTemplatePolicy.LegacyCatalogSchemaVersion,
            TemplatePreferences = new TemplatePreferences()
        };

        var normalized = SessionTemplatePolicy.Normalize(source);

        Assert.Equal(
            SessionTemplatePolicy.CatalogSchemaVersion,
            normalized.SchemaVersion);
        Assert.Equal(1.0, normalized.TemplatePreferences.MacroPlaybackSpeed);
        Assert.Equal(
            MacroRecordingHotkeyPolicy.DefaultValue,
            normalized.TemplatePreferences.MacroRecordingStopHotkey);
        Assert.False(SessionTemplatePolicy.AreEquivalent(source, normalized));
    }

    [Fact]
    public void Normalize_RejectsUnsupportedCatalogOrTemplateSchema()
    {
        var unsupportedCatalog = new SessionTemplateCatalog
        {
            SchemaVersion = SessionTemplatePolicy.CatalogSchemaVersion + 1
        };
        Assert.False(SessionTemplatePolicy.TryNormalize(
            unsupportedCatalog,
            out _));
        Assert.Throws<ArgumentException>(() =>
            SessionTemplatePolicy.Normalize(unsupportedCatalog));

        var catalog = new SessionTemplateCatalog
        {
            Templates =
            [
                new SessionTemplate
                {
                    SchemaVersion = 2,
                    Id = "future-template",
                    Name = "Future"
                },
                CreateTemplate("current-template")
            ]
        };

        var normalized = SessionTemplatePolicy.Normalize(catalog);
        Assert.Equal(
            "current-template",
            Assert.Single(normalized.Templates).Id);
    }

    [Fact]
    public void Normalize_DropsPlacementInsteadOfDowngradingMalformedStableMonitorIdentity()
    {
        var template = CreateTemplate("stable-monitor-template");
        template.ClientSlots[0].Placement = new NormalizedClientWindowPlacement
        {
            MonitorStableId = "monitor\nforged",
            MonitorDeviceName = @"\\.\DISPLAY2",
            MonitorIndex = 1,
            Left = 0.1,
            Top = 0.1,
            Width = 0.5,
            Height = 0.5
        };

        var normalized = SessionTemplatePolicy.Normalize(new SessionTemplateCatalog
        {
            Templates = [template]
        });

        Assert.Null(normalized.Templates[0].ClientSlots[0].Placement);
    }

    private static SessionTemplate CreateTemplate(
        string id,
        SessionTemplateMacroMode macroMode = SessionTemplateMacroMode.None,
        string? sharedMacroId = null,
        string? wholeMacroId = null,
        bool repeatWholeLayoutMacro = false) => new()
        {
            Id = id,
            Name = id,
            MacroMode = macroMode,
            SharedMacroId = sharedMacroId,
            WholeLayoutMacroId = wholeMacroId,
            RepeatWholeLayoutMacro = repeatWholeLayoutMacro,
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
                SlotId = $"{id}-slot",
                AccountKey = "saved-account"
            }
        ]
        };

    private static MacroDefinition CreateMacro(
        string id,
        string fileName) => new()
        {
            ContentId = id,
            SafeFileName = fileName,
            Name = "  Recorded   macro ",
            Kind = SessionMacroKind.Client,
            RecordedAccountKey = "saved-account",
            DurationMilliseconds = 1234,
            EventCount = 12,
            Sha256 = new string('a', 64),
            RecordedAtUtc = new DateTimeOffset(
            2026,
            8,
            3,
            10,
            0,
            0,
            TimeSpan.Zero)
        };
}
