using SessionDock.ExactWheel;
using SessionDock.Services;

namespace SessionDock.Models;

internal sealed class PortablePackageSelection
{
    internal IReadOnlyCollection<string> TemplateIds { get; init; } = [];

    internal IReadOnlyCollection<string> MacroContentIds { get; init; } = [];

    internal IReadOnlyCollection<string> NamedDestinationIds { get; init; } = [];

    // BatchLaunchPreset predates stable IDs. Its normalized, case-insensitively
    // unique name is therefore the selection ID until the persisted model gains
    // an intrinsic ID.
    internal IReadOnlyCollection<string> BatchPresetIds { get; init; } = [];
}

internal sealed record PortablePackageOmissionSummary(
    int NamedDestinations,
    int TemplateSlotDestinations);

internal sealed record PortableExportPackage(
    byte[] ArchiveBytes,
    string ManifestJson,
    int TemplateCount,
    int MacroCount,
    int NamedDestinationCount,
    int BatchPresetCount,
    PortablePackageOmissionSummary Omissions,
    IReadOnlyList<string> KeyboardMacroContentIds)
{
    internal const string SuggestedFileName =
        "SessionDock-portable.sessiondock";

    internal bool ContainsKeyboardInput =>
        KeyboardMacroContentIds.Count > 0;
}

internal sealed record PortableLayoutProfile(
    double TargetWidth,
    double TargetHeight,
    double MinimumWidth,
    double MinimumHeight,
    double RevealX,
    double RevealY);

internal sealed record PortableMacroBlob(
    string ContentId,
    string SafeFileName,
    string Sha256,
    SessionMacroKind Kind,
    byte[] Bytes,
    bool NeedsCatalogDefinition,
    int RecordedMonitorCount,
    int RecordedVirtualWidth,
    int RecordedVirtualHeight,
    bool HasKeyboardEvents,
    ExactWheelDisplayTopology RecordedDisplay);

internal sealed record PortableWholeLayoutAssignment(
    string TemplateId,
    string MacroContentId,
    int RecordedMonitorCount,
    int RecordedVirtualWidth,
    int RecordedVirtualHeight,
    ExactWheelDisplayTopology RecordedDisplay,
    bool IsAssigned,
    IReadOnlyList<PortableDeviceAdaptationReason> AdaptationReasons);

internal sealed record PortablePackageApplyResult(
    AppSettings Settings,
    SessionTemplateCatalog Catalog,
    IReadOnlyList<PortableMacroBlob> MacroBlobs);

internal sealed class PortableImportPlan
{
    private readonly PortablePackageApplyResult _prepared;

    internal PortableImportPlan(
        PortablePackageApplyResult prepared,
        PortableLayoutProfile layoutProfile,
        PortablePackageOmissionSummary omissions,
        IReadOnlyList<string> keyboardMacroContentIds,
        IReadOnlyList<PortableWholeLayoutAssignment> wholeLayoutAssignments,
        int importedTemplateCount,
        int skippedTemplateCount,
        int importedMacroCount,
        int deduplicatedMacroCount,
        int importedNamedDestinationCount,
        int importedBatchPresetCount,
        int unmatchedAccountReferenceCount)
    {
        _prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));
        LayoutProfile = layoutProfile ??
            throw new ArgumentNullException(nameof(layoutProfile));
        Omissions = omissions ?? throw new ArgumentNullException(nameof(omissions));
        KeyboardMacroContentIds = keyboardMacroContentIds ??
            throw new ArgumentNullException(nameof(keyboardMacroContentIds));
        WholeLayoutAssignments = wholeLayoutAssignments ??
            throw new ArgumentNullException(nameof(wholeLayoutAssignments));
        ImportedTemplateCount = importedTemplateCount;
        SkippedTemplateCount = skippedTemplateCount;
        ImportedMacroCount = importedMacroCount;
        DeduplicatedMacroCount = deduplicatedMacroCount;
        ImportedNamedDestinationCount = importedNamedDestinationCount;
        ImportedBatchPresetCount = importedBatchPresetCount;
        UnmatchedAccountReferenceCount = unmatchedAccountReferenceCount;
    }

    internal PortableLayoutProfile LayoutProfile { get; }

    internal PortablePackageOmissionSummary Omissions { get; }

    internal IReadOnlyList<string> KeyboardMacroContentIds { get; }

    internal bool ContainsKeyboardInput =>
        KeyboardMacroContentIds.Count > 0;

    internal IReadOnlyList<PortableWholeLayoutAssignment>
        WholeLayoutAssignments
    { get; }

    internal int UnassignedWholeLayoutMacroCount =>
        WholeLayoutAssignments.Count(assignment => !assignment.IsAssigned);

    internal int ImportedTemplateCount { get; }

    internal int SkippedTemplateCount { get; }

    internal int ImportedMacroCount { get; }

    internal int DeduplicatedMacroCount { get; }

    internal int ImportedNamedDestinationCount { get; }

    internal int ImportedBatchPresetCount { get; }

    internal int UnmatchedAccountReferenceCount { get; }

    internal bool HasChanges =>
        ImportedTemplateCount > 0 ||
        ImportedMacroCount > 0 ||
        ImportedNamedDestinationCount > 0 ||
        ImportedBatchPresetCount > 0;

    // This is deliberately pure. The caller can show the plan, obtain explicit
    // confirmation, then persist the three returned parts in a safe order.
    internal PortablePackageApplyResult Apply() =>
        PortableDataPackageService.CloneApplyResult(_prepared);
}
