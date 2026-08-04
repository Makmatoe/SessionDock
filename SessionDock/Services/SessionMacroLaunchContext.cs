using SessionDock.Models;

namespace SessionDock.Services;

internal sealed record LaunchedBatchClient(
    string AccountKey,
    RobloxClientProcessIdentity Identity);

internal sealed record SessionMacroClientTarget(
    string AccountKey,
    string DisplayName,
    int Order,
    RobloxClientProcessIdentity Identity,
    nint WindowHandle);

internal enum SessionMacroAssignmentIssueKind
{
    MissingMacro,
    WrongMacroKind,
    MissingClient,
    AmbiguousMacro,
    AmbiguousClient,
    WholeSessionNotReady
}

internal sealed record SessionMacroAssignmentIssue(
    SessionMacroAssignmentIssueKind Kind,
    string? AccountKey,
    string? MacroId);

internal sealed record SessionMacroLaunchSnapshot(
    string? TemplateId,
    string? TemplateName,
    IReadOnlyList<SessionMacroClientTarget> Clients,
    IReadOnlyDictionary<string, string> ClientMacroAssignments,
    string? WholeSessionMacroId,
    bool RepeatWholeSessionMacro)
{
    internal bool HasAssignments =>
        ClientMacroAssignments.Count > 0 ||
        !string.IsNullOrWhiteSpace(WholeSessionMacroId);
}

/// <summary>
/// Mutable state for one launched batch. Persistent objects keep stable account
/// and macro IDs; this runtime object additionally pins the exact verified
/// process identity and HWND discovered for that launch. Playback always takes
/// an immutable snapshot and revalidates those identities before input begins.
/// </summary>
internal sealed class SessionMacroLaunchContext
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<SessionMacroClientTarget> _clients;
    private readonly Dictionary<string, string> _clientAssignments;
    private string? _wholeSessionMacroId;
    private bool _repeatWholeSessionMacro;

    internal SessionMacroLaunchContext(
        string? templateId,
        string? templateName,
        IReadOnlyList<SessionMacroClientTarget> clients,
        IReadOnlyDictionary<string, string> clientAssignments,
        string? wholeSessionMacroId,
        bool repeatWholeSessionMacro)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(clientAssignments);
        TemplateId = string.IsNullOrWhiteSpace(templateId)
            ? null
            : templateId;
        TemplateName = string.IsNullOrWhiteSpace(templateName)
            ? null
            : templateName;
        _clients = clients
            .OrderBy(client => client.Order)
            .ToArray();
        _clientAssignments = new Dictionary<string, string>(
            clientAssignments,
            StringComparer.OrdinalIgnoreCase);
        _wholeSessionMacroId = string.IsNullOrWhiteSpace(wholeSessionMacroId)
            ? null
            : wholeSessionMacroId;
        _repeatWholeSessionMacro = _wholeSessionMacroId is not null &&
            repeatWholeSessionMacro;
    }

    internal string? TemplateId { get; }

    internal string? TemplateName { get; }

    internal event EventHandler? Changed;

    internal SessionMacroLaunchSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new SessionMacroLaunchSnapshot(
                TemplateId,
                TemplateName,
                _clients.ToArray(),
                new Dictionary<string, string>(
                    _clientAssignments,
                    StringComparer.OrdinalIgnoreCase),
                _wholeSessionMacroId,
                _repeatWholeSessionMacro);
        }
    }

    internal bool TrySetClientAssignment(
        string accountKey,
        MacroDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Kind != SessionMacroKind.Client ||
            string.IsNullOrWhiteSpace(definition.ContentId) ||
            _clients.Count(client => client.AccountKey.Equals(
                accountKey,
                StringComparison.OrdinalIgnoreCase)) != 1)
        {
            return false;
        }

        lock (_sync)
            _clientAssignments[accountKey] = definition.ContentId;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal bool RemoveClientAssignment(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        bool removed;
        lock (_sync)
            removed = _clientAssignments.Remove(accountKey);
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }
}

internal sealed record SessionMacroLaunchPlanResult(
    SessionMacroLaunchContext Context,
    IReadOnlyList<SessionMacroAssignmentIssue> Issues);

internal static class SessionMacroRuntimePlanner
{
    internal static SessionMacroLaunchPlanResult Create(
        SessionTemplate? template,
        IReadOnlyList<SessionMacroClientTarget> clients,
        IReadOnlyList<MacroDefinition> definitions,
        bool wholeLayoutCompletedSuccessfully = true)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(definitions);

        var issues = new List<SessionMacroAssignmentIssue>();
        var accountUniqueClients = clients
            .Where(client =>
                client is not null &&
                !string.IsNullOrWhiteSpace(client.AccountKey) &&
                client.Identity is not null &&
                client.WindowHandle != nint.Zero)
            .GroupBy(client => client.AccountKey, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                if (group.Count() == 1)
                    return group;
                issues.Add(new SessionMacroAssignmentIssue(
                    SessionMacroAssignmentIssueKind.AmbiguousClient,
                    group.Key,
                    null));
                return [];
            })
            .OrderBy(client => client.Order)
            .ToArray();
        var aliasedAccountKeys = accountUniqueClients
            .GroupBy(client => client.WindowHandle)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(client => client.AccountKey))
            .Concat(accountUniqueClients
                .GroupBy(
                    client => client.Identity,
                    RobloxClientProcessIdentityComparer.Instance)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group.Select(client => client.AccountKey)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var accountKey in aliasedAccountKeys)
        {
            issues.Add(new SessionMacroAssignmentIssue(
                SessionMacroAssignmentIssueKind.AmbiguousClient,
                accountKey,
                null));
        }

        var usableClients = accountUniqueClients
            .Where(client => !aliasedAccountKeys.Contains(client.AccountKey))
            .ToArray();
        var clientKeys = usableClients
            .Select(client => client.AccountKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wholeSessionReady = wholeLayoutCompletedSuccessfully &&
            HasCompleteUniqueClientSet(template, clients, clientKeys);
        var clientAssignments = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        string? wholeSessionMacroId = null;
        var repeatWholeSessionMacro = false;

        if (template is not null)
        {
            var resolution = SessionTemplateMacroAssignmentPolicy.Resolve(
                template,
                new SessionTemplateCatalog
                {
                    MacroDefinitions = [.. definitions]
                });
            issues.AddRange(resolution.InvalidAssignments.Select(invalid =>
                new SessionMacroAssignmentIssue(
                    ConvertIssue(invalid.IssueKind),
                    invalid.AccountKey,
                    invalid.MacroId)));
            foreach (var assignment in resolution.ValidAssignments)
            {
                if (assignment.ExpectedKind == SessionMacroKind.Client)
                {
                    if (assignment.AccountKey is null ||
                        !clientKeys.Contains(assignment.AccountKey))
                    {
                        issues.Add(new SessionMacroAssignmentIssue(
                            SessionMacroAssignmentIssueKind.MissingClient,
                            assignment.AccountKey,
                            assignment.MacroId));
                        continue;
                    }
                    clientAssignments[assignment.AccountKey] =
                        assignment.Definition.ContentId;
                    continue;
                }

                if (wholeSessionReady)
                {
                    wholeSessionMacroId = assignment.Definition.ContentId;
                    repeatWholeSessionMacro =
                        template.RepeatWholeLayoutMacro;
                }
                else
                {
                    issues.Add(new SessionMacroAssignmentIssue(
                        SessionMacroAssignmentIssueKind.WholeSessionNotReady,
                        null,
                        assignment.MacroId));
                }
            }
        }

        return new SessionMacroLaunchPlanResult(
            new SessionMacroLaunchContext(
                template?.Id,
                template?.Name,
                usableClients,
                clientAssignments,
                wholeSessionMacroId,
                repeatWholeSessionMacro),
            issues);
    }

    private static bool HasCompleteUniqueClientSet(
        SessionTemplate? template,
        IReadOnlyList<SessionMacroClientTarget> suppliedClients,
        IReadOnlySet<string> usableClientKeys)
    {
        if (template is null || template.ClientSlots.Count == 0)
            return false;

        var expectedGroups = template.ClientSlots
            .Where(slot => !string.IsNullOrWhiteSpace(slot.AccountKey))
            .GroupBy(
                slot => slot.AccountKey,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (expectedGroups.Length != template.ClientSlots.Count ||
            expectedGroups.Any(group => group.Count() != 1))
        {
            return false;
        }

        var suppliedGroups = suppliedClients
            .Where(client => client is not null)
            .GroupBy(
                client => client.AccountKey,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (suppliedGroups.Any(group => group.Count() != 1) ||
            suppliedGroups.Length != expectedGroups.Length ||
            usableClientKeys.Count != expectedGroups.Length)
        {
            return false;
        }

        var supplied = suppliedGroups
            .Select(group => group.Single())
            .ToArray();
        if (supplied.Select(client => client.WindowHandle).Distinct().Count() !=
                supplied.Length ||
            supplied.Select(client => client.Identity).Distinct(
                RobloxClientProcessIdentityComparer.Instance).Count() !=
                supplied.Length)
        {
            return false;
        }

        return expectedGroups.All(group =>
            usableClientKeys.Contains(group.Key));
    }

    private static SessionMacroAssignmentIssueKind ConvertIssue(
        SessionTemplateMacroAssignmentIssueKind issue) =>
        issue switch
        {
            SessionTemplateMacroAssignmentIssueKind.KindMismatch =>
                SessionMacroAssignmentIssueKind.WrongMacroKind,
            SessionTemplateMacroAssignmentIssueKind.AmbiguousDefinition =>
                SessionMacroAssignmentIssueKind.AmbiguousMacro,
            SessionTemplateMacroAssignmentIssueKind.DuplicateClientTarget =>
                SessionMacroAssignmentIssueKind.AmbiguousClient,
            SessionTemplateMacroAssignmentIssueKind.InvalidClientTarget =>
                SessionMacroAssignmentIssueKind.MissingClient,
            _ => SessionMacroAssignmentIssueKind.MissingMacro
        };
}
