using System.Text.Json.Serialization;

namespace SessionDock.Models;

public sealed class SessionTemplateCatalog
{
    public int SchemaVersion { get; set; } = 3;
    public List<SessionTemplate> Templates { get; set; } = [];
    public List<MacroDefinition> MacroDefinitions { get; set; } = [];
    public TemplatePreferences TemplatePreferences { get; set; } = new();
}

public sealed class SessionTemplate
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public int DelaySeconds { get; set; } = 8;
    public SessionTemplateLayoutMode LayoutMode { get; set; } =
        SessionTemplateLayoutMode.Cascade;
    public SessionTemplateMacroMode MacroMode { get; set; } =
        SessionTemplateMacroMode.None;
    public List<SessionTemplateClientSlot> ClientSlots { get; set; } = [];
    public string? SharedMacroId { get; set; }
    // Null is the schema-v1 compatibility value and means every client slot.
    // A non-null list is an explicit, fail-closed shared-macro target set.
    public List<string>? SharedMacroAccountKeys { get; set; }
    public string? WholeLayoutMacroId { get; set; }
    public bool RepeatWholeLayoutMacro { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? LegacyPresetName { get; set; }
}

public sealed class SessionTemplateClientSlot
{
    public string SlotId { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountKey { get; set; } = string.Empty;
    public int Order { get; set; }
    public string? Destination { get; set; }
    public NormalizedClientWindowPlacement? Placement { get; set; }
    public string? PerClientMacroId { get; set; }
}

public sealed class NormalizedClientWindowPlacement
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MonitorStableId { get; set; }
    public string? MonitorDeviceName { get; set; }
    public int MonitorIndex { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class TemplatePreferences
{
    public bool AutoArrangeNormalBatch { get; set; } = true;
    public double TargetWidth { get; set; } = 800;
    public double TargetHeight { get; set; } = 600;
    public double MinimumWidth { get; set; } = 640;
    public double MinimumHeight { get; set; } = 480;
    public double RevealX { get; set; } = 56;
    public double RevealY { get; set; } = 36;
    public string? PreferredMonitorDeviceName { get; set; }
    public double MacroPlaybackSpeed { get; set; } = 1.0;
    public string MacroRecordingStopHotkey { get; set; } = "F8";
}

public sealed class MacroDefinition
{
    public string ContentId { get; set; } = string.Empty;
    public string SafeFileName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public SessionMacroKind Kind { get; set; } = SessionMacroKind.Client;
    public string? RecordedAccountKey { get; set; }
    public long DurationMilliseconds { get; set; }
    public int EventCount { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset RecordedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum SessionTemplateLayoutMode
{
    Cascade,
    Saved
}

public enum SessionTemplateMacroMode
{
    None,
    PerClient,
    Shared,
    WholeLayout
}

public enum SessionMacroKind
{
    Client,
    WholeLayout
}
