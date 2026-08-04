using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SessionMacroRuntimePlannerTests
{
    private static readonly RobloxClientProcessIdentity FirstIdentity = new(
        101,
        new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc),
        @"C:\Roblox\version-a\RobloxPlayerBeta.exe");
    private static readonly RobloxClientProcessIdentity SecondIdentity = new(
        202,
        new DateTime(2026, 8, 3, 10, 0, 1, DateTimeKind.Utc),
        @"C:\Roblox\version-b\RobloxPlayerBeta.exe");

    [Fact]
    public void Create_MapsDifferentClientMacrosByStableAccountNotLaunchOrder()
    {
        var template = PerClientTemplate(
            ("account-a", "macro-a"),
            ("account-b", "macro-b"));

        var result = SessionMacroRuntimePlanner.Create(
            template,
            [Client("account-b", 1), Client("account-a", 0)],
            [Macro("macro-a"), Macro("macro-b")]);

        var snapshot = result.Context.Snapshot();
        Assert.Empty(result.Issues);
        Assert.Equal("macro-a", snapshot.ClientMacroAssignments["account-a"]);
        Assert.Equal("macro-b", snapshot.ClientMacroAssignments["account-b"]);
        Assert.Equal(
            ["account-a", "account-b"],
            snapshot.Clients.Select(client => client.AccountKey));
    }

    [Fact]
    public void Create_SkipsDeletedAndWrongKindAssignmentsButKeepsValidOnes()
    {
        var template = PerClientTemplate(
            ("account-a", "macro-valid"),
            ("account-b", "macro-whole"),
            ("account-c", "macro-deleted"));

        var result = SessionMacroRuntimePlanner.Create(
            template,
            [Client("account-a", 0), Client("account-b", 1)],
            [
                Macro("macro-valid"),
                Macro("macro-whole", SessionMacroKind.WholeLayout)
            ]);

        var snapshot = result.Context.Snapshot();
        Assert.Single(snapshot.ClientMacroAssignments);
        Assert.Equal(
            "macro-valid",
            snapshot.ClientMacroAssignments["account-a"]);
        Assert.Contains(
            result.Issues,
            issue => issue.Kind ==
                SessionMacroAssignmentIssueKind.WrongMacroKind);
        Assert.Contains(
            result.Issues,
            issue => issue.Kind ==
                SessionMacroAssignmentIssueKind.MissingMacro);
    }

    [Fact]
    public void Create_ReportsMissingClientForOtherwiseValidAssignment()
    {
        var result = SessionMacroRuntimePlanner.Create(
            PerClientTemplate(("missing-account", "macro-valid")),
            [Client("account-a", 0)],
            [Macro("macro-valid")]);

        var snapshot = result.Context.Snapshot();
        Assert.Empty(snapshot.ClientMacroAssignments);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(
            SessionMacroAssignmentIssueKind.MissingClient,
            issue.Kind);
        Assert.Equal("missing-account", issue.AccountKey);
        Assert.Equal("macro-valid", issue.MacroId);
    }

    [Fact]
    public void Create_ExpandsSharedMacroOnlyToExplicitTargetAccounts()
    {
        var template = new SessionTemplate
        {
            Id = "shared-template",
            Name = "Shared",
            MacroMode = SessionTemplateMacroMode.Shared,
            SharedMacroId = "shared-client-macro",
            SharedMacroAccountKeys = ["account-b"],
            ClientSlots =
            [
                Slot("account-a", 0),
                Slot("account-b", 1)
            ]
        };

        var result = SessionMacroRuntimePlanner.Create(
            template,
            [Client("account-a", 0), Client("account-b", 1)],
            [Macro("shared-client-macro")]);

        var assignment = Assert.Single(
            result.Context.Snapshot().ClientMacroAssignments);
        Assert.Equal("account-b", assignment.Key);
        Assert.Equal("shared-client-macro", assignment.Value);
    }

    [Fact]
    public void Context_RejectsWholeSessionMacroInClientAssignmentWorkflow()
    {
        var context = SessionMacroRuntimePlanner.Create(
            template: null,
            [Client("account-a", 0)],
            []).Context;

        var accepted = context.TrySetClientAssignment(
            "account-a",
            Macro("whole", SessionMacroKind.WholeLayout));

        Assert.False(accepted);
        Assert.Empty(context.Snapshot().ClientMacroAssignments);
    }

    [Fact]
    public void Context_AllowsChangeAndRemovalWithoutStartingPlayback()
    {
        var context = SessionMacroRuntimePlanner.Create(
            template: null,
            [Client("account-a", 0)],
            []).Context;

        Assert.True(context.TrySetClientAssignment(
            "account-a",
            Macro("first")));
        Assert.True(context.TrySetClientAssignment(
            "account-a",
            Macro("second")));
        Assert.Equal(
            "second",
            context.Snapshot().ClientMacroAssignments["account-a"]);
        Assert.True(context.RemoveClientAssignment("account-a"));
        Assert.False(context.Snapshot().HasAssignments);
    }

    [Fact]
    public void Context_PreservesWholeMacroWhenClientAssignmentIsAdded()
    {
        var context = SessionMacroRuntimePlanner.Create(
            new SessionTemplate
            {
                Id = "combined-runtime",
                Name = "Combined runtime",
                MacroMode = SessionTemplateMacroMode.WholeLayout,
                WholeLayoutMacroId = "whole",
                RepeatWholeLayoutMacro = true,
                ClientSlots = [Slot("account-a", 0)]
            },
            [Client("account-a", 0)],
            [Macro("whole", SessionMacroKind.WholeLayout)]).Context;

        Assert.True(context.TrySetClientAssignment(
            "account-a",
            Macro("client")));
        var snapshot = context.Snapshot();

        Assert.Equal("client", snapshot.ClientMacroAssignments["account-a"]);
        Assert.Equal("whole", snapshot.WholeSessionMacroId);
        Assert.True(snapshot.RepeatWholeSessionMacro);
    }

    [Fact]
    public void Create_SuppressesWholeMacroWhenExpectedClientIsMissing()
    {
        var result = SessionMacroRuntimePlanner.Create(
            WholeLayoutTemplate("account-a", "account-b"),
            [Client("account-a", 0)],
            [Macro("whole", SessionMacroKind.WholeLayout)]);

        var snapshot = result.Context.Snapshot();
        Assert.Null(snapshot.WholeSessionMacroId);
        Assert.Contains(
            result.Issues,
            issue => issue.Kind ==
                SessionMacroAssignmentIssueKind.WholeSessionNotReady);
    }

    [Fact]
    public void Create_SuppressesWholeMacroWhenLayoutDidNotComplete()
    {
        var result = SessionMacroRuntimePlanner.Create(
            WholeLayoutTemplate("account-a", "account-b"),
            [Client("account-a", 0), Client("account-b", 1)],
            [Macro("whole", SessionMacroKind.WholeLayout)],
            wholeLayoutCompletedSuccessfully: false);

        Assert.Null(result.Context.Snapshot().WholeSessionMacroId);
        Assert.Contains(
            result.Issues,
            issue => issue.Kind ==
                SessionMacroAssignmentIssueKind.WholeSessionNotReady);
    }

    [Fact]
    public void Create_SuppressesWholeMacroWhenAccountsShareOneWindow()
    {
        var first = Client("account-a", 0);
        var second = Client("account-b", 1) with
        {
            WindowHandle = first.WindowHandle
        };
        var result = SessionMacroRuntimePlanner.Create(
            WholeLayoutTemplate("account-a", "account-b"),
            [first, second],
            [Macro("whole", SessionMacroKind.WholeLayout)]);

        Assert.Null(result.Context.Snapshot().WholeSessionMacroId);
        Assert.Contains(
            result.Issues,
            issue => issue.Kind ==
                SessionMacroAssignmentIssueKind.WholeSessionNotReady);
    }

    [Fact]
    public void Create_SuppressesPerClientMacrosWhenAccountsShareOneWindow()
    {
        var first = Client("account-a", 0);
        var second = Client("account-b", 1) with
        {
            WindowHandle = first.WindowHandle
        };
        var result = SessionMacroRuntimePlanner.Create(
            PerClientTemplate(
                ("account-a", "macro-a"),
                ("account-b", "macro-b")),
            [first, second],
            [Macro("macro-a"), Macro("macro-b")]);

        var snapshot = result.Context.Snapshot();
        Assert.Empty(snapshot.Clients);
        Assert.Empty(snapshot.ClientMacroAssignments);
        Assert.Equal(
            2,
            result.Issues.Count(issue => issue.Kind ==
                SessionMacroAssignmentIssueKind.AmbiguousClient));
    }

    [Fact]
    public void Create_AllowsWholeMacroOnlyForCompleteUniqueLaidOutSet()
    {
        var result = SessionMacroRuntimePlanner.Create(
            WholeLayoutTemplate("account-a", "account-b"),
            [Client("account-b", 1), Client("account-a", 0)],
            [Macro("whole", SessionMacroKind.WholeLayout)],
            wholeLayoutCompletedSuccessfully: true);

        Assert.Equal(
            "whole",
            result.Context.Snapshot().WholeSessionMacroId);
        Assert.DoesNotContain(
            result.Issues,
            issue => issue.Kind ==
                SessionMacroAssignmentIssueKind.WholeSessionNotReady);
    }

    [Fact]
    public void Create_LayoutFailureDoesNotSuppressValidPerClientMacros()
    {
        var result = SessionMacroRuntimePlanner.Create(
            PerClientTemplate(("account-a", "macro-a")),
            [Client("account-a", 0)],
            [Macro("macro-a")],
            wholeLayoutCompletedSuccessfully: false);

        Assert.Equal(
            "macro-a",
            result.Context.Snapshot().ClientMacroAssignments["account-a"]);
    }

    [Fact]
    public void Create_RejectsAmbiguousClientMapping()
    {
        var result = SessionMacroRuntimePlanner.Create(
            PerClientTemplate(("account-a", "macro-a")),
            [Client("account-a", 0), Client("account-a", 1)],
            [Macro("macro-a")]);

        Assert.Empty(result.Context.Snapshot().Clients);
        Assert.Contains(
            result.Issues,
            issue => issue.Kind ==
                SessionMacroAssignmentIssueKind.AmbiguousClient);
    }

    private static SessionMacroClientTarget Client(
        string accountKey,
        int order) =>
        new(
            accountKey,
            accountKey,
            order,
            order == 0 ? FirstIdentity : SecondIdentity,
            new nint(order + 100));

    private static MacroDefinition Macro(
        string id,
        SessionMacroKind kind = SessionMacroKind.Client) =>
        new()
        {
            ContentId = id,
            SafeFileName = new string('a', 64) + ".ewmacro",
            Name = id,
            Kind = kind,
            Sha256 = new string('A', 64)
        };

    private static SessionTemplate PerClientTemplate(
        params (string AccountKey, string MacroId)[] assignments) =>
        new()
        {
            Id = "per-client-template",
            Name = "Per client",
            MacroMode = SessionTemplateMacroMode.PerClient,
            ClientSlots = assignments
                .Select((assignment, order) => new SessionTemplateClientSlot
                {
                    SlotId = $"slot-{order}",
                    AccountKey = assignment.AccountKey,
                    Order = order,
                    PerClientMacroId = assignment.MacroId
                })
                .ToList()
        };

    private static SessionTemplate WholeLayoutTemplate(
        params string[] accountKeys) =>
        new()
        {
            Id = "whole-template",
            Name = "Whole layout",
            MacroMode = SessionTemplateMacroMode.WholeLayout,
            WholeLayoutMacroId = "whole",
            ClientSlots = accountKeys
                .Select((accountKey, order) => Slot(accountKey, order))
                .ToList()
        };

    private static SessionTemplateClientSlot Slot(
        string accountKey,
        int order) =>
        new()
        {
            SlotId = $"slot-{order}",
            AccountKey = accountKey,
            Order = order
        };
}
