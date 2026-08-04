using SessionDock.Models;

namespace SessionDock.Services;

internal enum SessionTemplateMacroAssignmentIssueKind
{
    InvalidMacroMode,
    MissingMacroId,
    InvalidClientTarget,
    DuplicateClientTarget,
    MissingDefinition,
    AmbiguousDefinition,
    KindMismatch
}

internal sealed record ResolvedSessionTemplateMacroAssignment(
    SessionTemplateMacroMode MacroMode,
    string? SlotId,
    string? AccountKey,
    int Order,
    string MacroId,
    SessionMacroKind ExpectedKind,
    MacroDefinition Definition);

internal sealed record InvalidSessionTemplateMacroAssignment(
    SessionTemplateMacroMode MacroMode,
    string? SlotId,
    string? AccountKey,
    int Order,
    string? MacroId,
    SessionMacroKind? ExpectedKind,
    SessionTemplateMacroAssignmentIssueKind IssueKind);

internal sealed record SessionTemplateMacroAssignmentResolution(
    IReadOnlyList<ResolvedSessionTemplateMacroAssignment> ValidAssignments,
    IReadOnlyList<InvalidSessionTemplateMacroAssignment> InvalidAssignments)
{
    internal bool IsFullyValid => InvalidAssignments.Count == 0;
}

internal static class SessionTemplateMacroAssignmentPolicy
{
    internal static SessionTemplateMacroAssignmentResolution Resolve(
        SessionTemplate template,
        SessionTemplateCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(catalog);

        var valid = new List<ResolvedSessionTemplateMacroAssignment>();
        var invalid = new List<InvalidSessionTemplateMacroAssignment>();
        var definitions = BuildDefinitionLookup(catalog);

        void ResolveReference(
            SessionTemplateMacroMode macroMode,
            SessionTemplateClientSlot? slot,
            string? accountKey,
            int order,
            string? macroId,
            SessionMacroKind expectedKind)
        {
            if (string.IsNullOrWhiteSpace(macroId))
            {
                invalid.Add(new(
                    macroMode,
                    slot?.SlotId,
                    accountKey,
                    order,
                    macroId,
                    expectedKind,
                    SessionTemplateMacroAssignmentIssueKind.MissingMacroId));
                return;
            }

            if (!definitions.TryGetValue(macroId, out var matches))
            {
                invalid.Add(new(
                    macroMode,
                    slot?.SlotId,
                    accountKey,
                    order,
                    macroId,
                    expectedKind,
                    SessionTemplateMacroAssignmentIssueKind.MissingDefinition));
                return;
            }
            if (matches.Count != 1)
            {
                invalid.Add(new(
                    macroMode,
                    slot?.SlotId,
                    accountKey,
                    order,
                    macroId,
                    expectedKind,
                    SessionTemplateMacroAssignmentIssueKind
                        .AmbiguousDefinition));
                return;
            }

            var definition = matches[0];
            if (!Enum.IsDefined(definition.Kind) ||
                definition.Kind != expectedKind)
            {
                invalid.Add(new(
                    macroMode,
                    slot?.SlotId,
                    accountKey,
                    order,
                    macroId,
                    expectedKind,
                    SessionTemplateMacroAssignmentIssueKind.KindMismatch));
                return;
            }

            valid.Add(new(
                macroMode,
                slot?.SlotId,
                accountKey,
                order,
                macroId,
                expectedKind,
                definition));
        }

        switch (template.MacroMode)
        {
            case SessionTemplateMacroMode.None:
                break;
            case SessionTemplateMacroMode.PerClient:
                ResolvePerClient(
                    template,
                    ResolveReference,
                    invalid);
                break;
            case SessionTemplateMacroMode.Shared:
                ResolveShared(
                    template,
                    ResolveReference,
                    invalid);
                break;
            case SessionTemplateMacroMode.WholeLayout:
                ResolveReference(
                    template.MacroMode,
                    null,
                    null,
                    0,
                    template.WholeLayoutMacroId,
                    SessionMacroKind.WholeLayout);
                break;
            default:
                invalid.Add(new(
                    template.MacroMode,
                    null,
                    null,
                    0,
                    null,
                    null,
                    SessionTemplateMacroAssignmentIssueKind.InvalidMacroMode));
                break;
        }

        return new(valid.ToArray(), invalid.ToArray());
    }

    private static void ResolvePerClient(
        SessionTemplate template,
        Action<
            SessionTemplateMacroMode,
            SessionTemplateClientSlot?,
            string?,
            int,
            string?,
            SessionMacroKind> resolveReference,
        ICollection<InvalidSessionTemplateMacroAssignment> invalid)
    {
        var sawMacroReference = false;
        var accountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in OrderedSlots(template.ClientSlots))
        {
            var slot = item.Slot;
            if (slot is null ||
                string.IsNullOrWhiteSpace(slot.PerClientMacroId))
            {
                continue;
            }

            sawMacroReference = true;
            if (string.IsNullOrWhiteSpace(slot.AccountKey))
            {
                invalid.Add(InvalidTarget(
                    template.MacroMode,
                    slot,
                    item.Order,
                    slot.PerClientMacroId,
                    SessionTemplateMacroAssignmentIssueKind.InvalidClientTarget));
                continue;
            }
            if (!accountKeys.Add(slot.AccountKey))
            {
                invalid.Add(InvalidTarget(
                    template.MacroMode,
                    slot,
                    item.Order,
                    slot.PerClientMacroId,
                    SessionTemplateMacroAssignmentIssueKind.DuplicateClientTarget));
                continue;
            }

            resolveReference(
                template.MacroMode,
                slot,
                slot.AccountKey,
                item.Order,
                slot.PerClientMacroId,
                SessionMacroKind.Client);
        }

        if (!sawMacroReference)
        {
            invalid.Add(new(
                template.MacroMode,
                null,
                null,
                0,
                null,
                SessionMacroKind.Client,
                SessionTemplateMacroAssignmentIssueKind.MissingMacroId));
        }
    }

    private static void ResolveShared(
        SessionTemplate template,
        Action<
            SessionTemplateMacroMode,
            SessionTemplateClientSlot?,
            string?,
            int,
            string?,
            SessionMacroKind> resolveReference,
        ICollection<InvalidSessionTemplateMacroAssignment> invalid)
    {
        var slotsByAccount = new Dictionary<
            string,
            OrderedSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in OrderedSlots(template.ClientSlots))
        {
            var slot = item.Slot;
            if (slot is null || string.IsNullOrWhiteSpace(slot.AccountKey))
            {
                invalid.Add(InvalidTarget(
                    template.MacroMode,
                    slot,
                    item.Order,
                    template.SharedMacroId,
                    SessionTemplateMacroAssignmentIssueKind.InvalidClientTarget));
                continue;
            }
            if (!slotsByAccount.TryAdd(slot.AccountKey, item))
            {
                invalid.Add(InvalidTarget(
                    template.MacroMode,
                    slot,
                    item.Order,
                    template.SharedMacroId,
                    SessionTemplateMacroAssignmentIssueKind.DuplicateClientTarget));
            }
        }

        var targets = new List<OrderedSlot>();
        if (template.SharedMacroAccountKeys is null)
        {
            targets.AddRange(slotsByAccount.Values.OrderBy(item => item.Order));
        }
        else
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var accountKey in template.SharedMacroAccountKeys)
            {
                if (string.IsNullOrWhiteSpace(accountKey) ||
                    !slotsByAccount.TryGetValue(accountKey, out var target))
                {
                    invalid.Add(new(
                        template.MacroMode,
                        null,
                        accountKey,
                        0,
                        template.SharedMacroId,
                        SessionMacroKind.Client,
                        SessionTemplateMacroAssignmentIssueKind.InvalidClientTarget));
                    continue;
                }
                if (!selected.Add(accountKey))
                {
                    invalid.Add(InvalidTarget(
                        template.MacroMode,
                        target.Slot,
                        target.Order,
                        template.SharedMacroId,
                        SessionTemplateMacroAssignmentIssueKind.DuplicateClientTarget));
                    continue;
                }
                targets.Add(target);
            }
        }

        if (targets.Count == 0)
        {
            if (invalid.Count == 0)
            {
                invalid.Add(new(
                    template.MacroMode,
                    null,
                    null,
                    0,
                    template.SharedMacroId,
                    SessionMacroKind.Client,
                    SessionTemplateMacroAssignmentIssueKind.InvalidClientTarget));
            }
            return;
        }

        foreach (var target in targets.OrderBy(item => item.Order))
        {
            resolveReference(
                template.MacroMode,
                target.Slot,
                target.Slot!.AccountKey,
                target.Order,
                template.SharedMacroId,
                SessionMacroKind.Client);
        }
    }

    private static InvalidSessionTemplateMacroAssignment InvalidTarget(
        SessionTemplateMacroMode macroMode,
        SessionTemplateClientSlot? slot,
        int order,
        string? macroId,
        SessionTemplateMacroAssignmentIssueKind issueKind) =>
        new(
            macroMode,
            slot?.SlotId,
            slot?.AccountKey,
            order,
            macroId,
            SessionMacroKind.Client,
            issueKind);

    private static IReadOnlyDictionary<string, List<MacroDefinition>>
        BuildDefinitionLookup(SessionTemplateCatalog catalog)
    {
        var definitions = new Dictionary<
            string,
            List<MacroDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in catalog.MacroDefinitions ?? [])
        {
            if (definition is null ||
                string.IsNullOrWhiteSpace(definition.ContentId))
            {
                continue;
            }
            if (!definitions.TryGetValue(
                    definition.ContentId,
                    out var matches))
            {
                matches = [];
                definitions.Add(definition.ContentId, matches);
            }
            matches.Add(definition);
        }
        return definitions;
    }

    private static IEnumerable<OrderedSlot> OrderedSlots(
        IReadOnlyList<SessionTemplateClientSlot>? slots) =>
        (slots ?? [])
        .Select((slot, index) => new OrderedSlot(slot, slot?.Order ?? index, index))
        .OrderBy(item => item.Order)
        .ThenBy(item => item.SourceIndex);

    private sealed record OrderedSlot(
        SessionTemplateClientSlot? Slot,
        int Order,
        int SourceIndex);
}
