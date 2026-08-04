using System.Security.Cryptography;
using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SessionTemplateMacroPreflightTests
{
    [Fact]
    public void Validate_ValidPerClientAssignments_LoadsEachArtifactOnce()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Shared bytes",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        var template = PerClientTemplate(
            ("account_1", definition.ContentId),
            ("account_2", definition.ContentId));
        var loadCount = 0;

        var result = SessionTemplateMacroPreflight.Validate(
            template,
            Catalog(definition),
            candidate =>
            {
                loadCount++;
                _ = store.Load(candidate);
            });

        Assert.True(result.Success);
        Assert.Equal(
            SessionTemplateMacroPreflightFailureKind.None,
            result.FailureKind);
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public void Validate_StaleReference_FailsAsInvalidAssignmentWithoutLoading()
    {
        var loadCalled = false;

        var result = SessionTemplateMacroPreflight.Validate(
            PerClientTemplate(("account_1", "stale-macro-id")),
            Catalog(),
            _ => loadCalled = true);

        Assert.False(result.Success);
        Assert.Equal(
            SessionTemplateMacroPreflightFailureKind.InvalidAssignment,
            result.FailureKind);
        Assert.Equal("stale-macro-id", result.MacroId);
        Assert.False(loadCalled);
    }

    [Fact]
    public void Validate_UnknownMode_FailsBeforeLoading()
    {
        var template = PerClientTemplate(("account_1", "macro-id"));
        template.MacroMode = (SessionTemplateMacroMode)int.MaxValue;
        var loadCalled = false;

        var result = SessionTemplateMacroPreflight.Validate(
            template,
            Catalog(),
            _ => loadCalled = true);

        Assert.False(result.Success);
        Assert.Equal(
            SessionTemplateMacroPreflightFailureKind.InvalidAssignment,
            result.FailureKind);
        Assert.False(loadCalled);
    }

    [Fact]
    public void Validate_WrongKind_FailsBeforeLoading()
    {
        using var directory = new TemporaryDirectory();
        var definition = CreateStore(directory.Path).Save(
            "Whole layout",
            SessionMacroKind.WholeLayout,
            ExactWheelTestData.Recording());
        var loadCalled = false;

        var result = SessionTemplateMacroPreflight.Validate(
            PerClientTemplate(("account_1", definition.ContentId)),
            Catalog(definition),
            _ => loadCalled = true);

        Assert.False(result.Success);
        Assert.Equal(
            SessionTemplateMacroPreflightFailureKind.InvalidAssignment,
            result.FailureKind);
        Assert.False(loadCalled);
    }

    [Fact]
    public void Validate_MissingMacroFile_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Missing",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        File.Delete(MacroPath(directory.Path, definition));

        var result = SessionTemplateMacroPreflight.Validate(
            PerClientTemplate(("account_1", definition.ContentId)),
            Catalog(definition),
            store);

        AssertUnavailable(result, definition);
    }

    [Fact]
    public void Validate_UnsafeMacroPath_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Unsafe path",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        definition.SafeFileName = "..\\outside.ewmacro";

        var result = SessionTemplateMacroPreflight.Validate(
            PerClientTemplate(("account_1", definition.ContentId)),
            Catalog(definition),
            store);

        AssertUnavailable(result, definition);
    }

    [Fact]
    public void Validate_HashMismatch_FailsBeforePlayback()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Tampered",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        var path = MacroPath(directory.Path, definition);
        var bytes = File.ReadAllBytes(path);
        bytes[36] ^= 1;
        File.WriteAllBytes(path, bytes);

        var result = SessionTemplateMacroPreflight.Validate(
            PerClientTemplate(("account_1", definition.ContentId)),
            Catalog(definition),
            store);

        AssertUnavailable(result, definition);
    }

    [Fact]
    public void Validate_HashValidButCorruptPayload_FailsDeserialization()
    {
        using var directory = new TemporaryDirectory();
        var bytes = Enumerable.Repeat(
            (byte)0xA5,
            checked((int)(
                ExactWheelMacroSerializer.FixedHeaderBytes + sizeof(uint))))
            .ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var definition = new MacroDefinition
        {
            ContentId = "ew-client-" + hash.ToLowerInvariant(),
            SafeFileName = hash.ToLowerInvariant() + ".ewmacro",
            Name = "Corrupt",
            Kind = SessionMacroKind.Client,
            DurationMilliseconds = 0,
            EventCount = 0,
            Sha256 = hash
        };
        var macrosDirectory = Path.Combine(directory.Path, "Macros");
        Directory.CreateDirectory(macrosDirectory);
        File.WriteAllBytes(
            Path.Combine(macrosDirectory, definition.SafeFileName),
            bytes);

        var result = SessionTemplateMacroPreflight.Validate(
            PerClientTemplate(("account_1", definition.ContentId)),
            Catalog(definition),
            CreateStore(directory.Path));

        AssertUnavailable(result, definition);
    }

    [Fact]
    public void Validate_ClientMouseOutsideRecordedClient_FailsBeforeLaunch()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var events = ExactWheelTestData.Events();
        events[0] = events[0] with { X = 99 };
        var definition = store.Save(
            "Outside client",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording(events: events));

        var result = SessionTemplateMacroPreflight.Validate(
            PerClientTemplate(("account_1", definition.ContentId)),
            Catalog(definition),
            store);

        AssertUnavailable(result, definition);
    }

    [Fact]
    public void Validate_ZeroSizeRecordedClient_FailsBeforeLaunch()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Zero-size client",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording(
                target: ExactWheelTestData.Target(
                    client: new ExactWheelRect(100, 80, 100, 900))));

        var result = SessionTemplateMacroPreflight.Validate(
            PerClientTemplate(("account_1", definition.ContentId)),
            Catalog(definition),
            store);

        AssertUnavailable(result, definition);
    }

    [Fact]
    public void Validate_LegacySharedAllTargets_RemainsCompatible()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Legacy shared",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        var template = new SessionTemplate
        {
            MacroMode = SessionTemplateMacroMode.Shared,
            SharedMacroId = definition.ContentId,
            SharedMacroAccountKeys = null,
            ClientSlots =
            [
                Slot("account_1", 0),
                Slot("account_2", 1)
            ]
        };

        var result = SessionTemplateMacroPreflight.Validate(
            template,
            Catalog(definition),
            store);

        Assert.True(result.Success);
        Assert.Equal(
            2,
            SessionTemplatePolicy.SelectSharedMacroTargetSlots(template).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_ExplicitSharedTargetMustSelectAnExistingClient(
        bool useUnknownAccount)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Shared",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        var template = new SessionTemplate
        {
            MacroMode = SessionTemplateMacroMode.Shared,
            SharedMacroId = definition.ContentId,
            SharedMacroAccountKeys = useUnknownAccount
                ? ["unknown_account"]
                : [],
            ClientSlots = [Slot("account_1", 0)]
        };

        var result = SessionTemplateMacroPreflight.Validate(
            template,
            Catalog(definition),
            store);

        Assert.False(result.Success);
        Assert.Equal(
            SessionTemplateMacroPreflightFailureKind.InvalidAssignment,
            result.FailureKind);
    }

    [Fact]
    public void Validate_ValidWholeLayoutMacro_Passes()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Whole layout",
            SessionMacroKind.WholeLayout,
            ExactWheelTestData.Recording());
        var template = new SessionTemplate
        {
            MacroMode = SessionTemplateMacroMode.WholeLayout,
            WholeLayoutMacroId = definition.ContentId,
            ClientSlots = [Slot("account_1", 0)]
        };

        var result = SessionTemplateMacroPreflight.Validate(
            template,
            Catalog(definition),
            store);

        Assert.True(result.Success);
    }

    [Fact]
    public void Resolve_PreservesValidClientsAndReportsEachInvalidAssignment()
    {
        var valid = Definition("valid-client", SessionMacroKind.Client);
        var wrongKind = Definition(
            "wrong-kind",
            SessionMacroKind.WholeLayout);
        var ambiguousOne = Definition(
            "ambiguous",
            SessionMacroKind.Client);
        var ambiguousTwo = Definition(
            "ambiguous",
            SessionMacroKind.Client);
        var template = PerClientTemplate(
            ("account_1", valid.ContentId),
            ("account_2", "deleted"),
            ("account_3", wrongKind.ContentId),
            ("account_4", ambiguousOne.ContentId));
        var catalog = Catalog(
            valid,
            wrongKind,
            ambiguousOne,
            ambiguousTwo);

        var resolution = SessionTemplateMacroAssignmentPolicy.Resolve(
            template,
            catalog);

        Assert.False(resolution.IsFullyValid);
        var resolved = Assert.Single(resolution.ValidAssignments);
        Assert.Equal("account_1", resolved.AccountKey);
        Assert.Same(valid, resolved.Definition);
        Assert.Collection(
            resolution.InvalidAssignments,
            assignment =>
            {
                Assert.Equal("account_2", assignment.AccountKey);
                Assert.Equal(
                    SessionTemplateMacroAssignmentIssueKind.MissingDefinition,
                    assignment.IssueKind);
            },
            assignment =>
            {
                Assert.Equal("account_3", assignment.AccountKey);
                Assert.Equal(
                    SessionTemplateMacroAssignmentIssueKind.KindMismatch,
                    assignment.IssueKind);
            },
            assignment =>
            {
                Assert.Equal("account_4", assignment.AccountKey);
                Assert.Equal(
                    SessionTemplateMacroAssignmentIssueKind
                        .AmbiguousDefinition,
                    assignment.IssueKind);
            });

        var loadCalled = false;
        var preflight = SessionTemplateMacroPreflight.Validate(
            template,
            catalog,
            _ => loadCalled = true);
        Assert.False(preflight.Success);
        Assert.Equal("deleted", preflight.MacroId);
        Assert.False(loadCalled);
    }

    [Fact]
    public void Resolve_SharedTargetsRetainsKnownClientsAndReportsBadSelectors()
    {
        var definition = Definition("shared", SessionMacroKind.Client);
        var template = new SessionTemplate
        {
            MacroMode = SessionTemplateMacroMode.Shared,
            SharedMacroId = definition.ContentId,
            SharedMacroAccountKeys =
            [
                "account_1",
                "deleted_account",
                "account_1",
                "account_2"
            ],
            ClientSlots =
            [
                Slot("account_1", 0),
                Slot("account_2", 1)
            ]
        };

        var resolution = SessionTemplateMacroAssignmentPolicy.Resolve(
            template,
            Catalog(definition));

        Assert.Equal(
            ["account_1", "account_2"],
            resolution.ValidAssignments.Select(item => item.AccountKey));
        Assert.Collection(
            resolution.InvalidAssignments,
            assignment => Assert.Equal(
                SessionTemplateMacroAssignmentIssueKind.InvalidClientTarget,
                assignment.IssueKind),
            assignment => Assert.Equal(
                SessionTemplateMacroAssignmentIssueKind.DuplicateClientTarget,
                assignment.IssueKind));
    }

    [Fact]
    public void ExplicitPlayback_RevalidatesOnlyAfterPlayAndNeverPostLaunch()
    {
        var root = FindRepositoryRoot();
        var templatesSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Templates.cs"));
        var postLaunchStart = templatesSource.IndexOf(
            "private async Task<SessionPostLaunchResult> ApplySessionPostLaunchAsync",
            StringComparison.Ordinal);
        var postLaunchEnd = templatesSource.IndexOf(
            "private async Task<TemplateMacroPlaybackResult> PlayTemplateMacrosAsync",
            StringComparison.Ordinal);
        Assert.True(postLaunchStart >= 0);
        Assert.True(postLaunchEnd > postLaunchStart);
        var postLaunchSource = templatesSource[
            postLaunchStart..postLaunchEnd];
        Assert.DoesNotContain(
            "PlayTemplateMacrosAsync(",
            postLaunchSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Task.Delay(",
            postLaunchSource,
            StringComparison.Ordinal);

        var playbackSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.SessionMacros.cs"));
        var playEntry = playbackSource.IndexOf(
            "private async Task<SessionMacroPlaybackOutcome>",
            StringComparison.Ordinal);
        Assert.True(playEntry >= 0);
        var prepareCall = playbackSource.IndexOf(
            "PrepareRuntimeMacroPlan(",
            playEntry,
            StringComparison.Ordinal);
        var playCall = playbackSource.IndexOf(
            "PlayTemplateMacrosAsync(",
            playEntry,
            StringComparison.Ordinal);
        Assert.True(prepareCall > playEntry);
        Assert.True(playCall > prepareCall);
        Assert.Contains(
            "playbackCache.GetOrLoad(",
            playbackSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "static (store, candidate) => store.Load(candidate)",
            playbackSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateRun_PreflightGatesCancellationCloseAndLaunch()
    {
        var root = FindRepositoryRoot();
        var templatesSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Templates.cs"));
        var runStart = templatesSource.IndexOf(
            "private async Task RunTemplateButtonClickAsync(",
            StringComparison.Ordinal);
        Assert.True(runStart >= 0);
        var runEnd = templatesSource.IndexOf(
            "private async void RecordMacroButtonClick(",
            runStart,
            StringComparison.Ordinal);
        Assert.True(runEnd > runStart);
        var runSource = templatesSource[runStart..runEnd];

        var preflight = runSource.IndexOf(
            "PreflightTemplateMacros(template)",
            StringComparison.Ordinal);
        var failureGate = runSource.IndexOf(
            "if (!macroPreflight.Success)",
            preflight,
            StringComparison.Ordinal);
        var failureResult = runSource.IndexOf(
            "MacroPreflightFailure: macroPreflight.FailureKind",
            failureGate,
            StringComparison.Ordinal);
        var failureReturn = runSource.IndexOf(
            "return;",
            failureResult,
            StringComparison.Ordinal);
        var destinationFlush = runSource.IndexOf(
            "FlushDestinationPersistenceAsync()",
            StringComparison.Ordinal);
        var mutation = runSource.IndexOf(
            "ClearBatchRetryState();",
            StringComparison.Ordinal);
        var runBatch = runSource.IndexOf(
            "RunBatchLaunchAsync(",
            StringComparison.Ordinal);

        Assert.True(preflight >= 0);
        Assert.True(failureGate > preflight);
        Assert.True(failureResult > failureGate);
        Assert.True(failureReturn > failureResult);
        Assert.True(destinationFlush > failureReturn);
        Assert.True(mutation > failureReturn);
        Assert.True(runBatch > mutation);

        var batchSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Batch.cs"));
        var batchStart = batchSource.IndexOf(
            "private async Task<BatchLaunchResult> RunBatchLaunchAsync(",
            StringComparison.Ordinal);
        Assert.True(batchStart >= 0);
        var cancelPlayback = batchSource.IndexOf(
            "CancelAndWaitForCurrentMacroPlaybackAsync",
            batchStart,
            StringComparison.Ordinal);
        var closePlayers = batchSource.IndexOf(
            "CloseAllPlayersAsync(",
            batchStart,
            StringComparison.Ordinal);
        var launchAccounts = batchSource.IndexOf(
            "QueueBatchLaunchAsync(",
            batchStart,
            StringComparison.Ordinal);
        Assert.True(cancelPlayback > batchStart);
        Assert.True(closePlayers > cancelPlayback);
        Assert.True(launchAccounts > closePlayers);
    }

    [Fact]
    public void RuntimePlanner_SkipsInvalidAssignmentsAndKeepsValidClients()
    {
        var valid = Definition("valid-client", SessionMacroKind.Client);
        var wrongKind = Definition(
            "wrong-kind",
            SessionMacroKind.WholeLayout);
        var duplicateOne = Definition(
            "ambiguous",
            SessionMacroKind.Client);
        var duplicateTwo = Definition(
            "ambiguous",
            SessionMacroKind.Client);
        var template = PerClientTemplate(
            ("account_1", valid.ContentId),
            ("account_2", "deleted"),
            ("account_3", wrongKind.ContentId),
            ("account_4", duplicateOne.ContentId));
        var clients = template.ClientSlots.Select(slot =>
            new SessionMacroClientTarget(
                slot.AccountKey,
                slot.AccountKey,
                slot.Order,
                new RobloxClientProcessIdentity(
                    slot.Order + 1,
                    new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc),
                    @"C:\RobloxPlayerBeta.exe"),
                new nint(slot.Order + 1))).ToArray();

        var result = SessionMacroRuntimePlanner.Create(
            template,
            clients,
            [valid, wrongKind, duplicateOne, duplicateTwo]);
        var snapshot = result.Context.Snapshot();

        var assignment = Assert.Single(snapshot.ClientMacroAssignments);
        Assert.Equal("account_1", assignment.Key);
        Assert.Equal(valid.ContentId, assignment.Value);
        Assert.Equal(3, result.Issues.Count);
        Assert.Contains(
            result.Issues,
            issue => issue.Kind == SessionMacroAssignmentIssueKind.MissingMacro);
        Assert.Contains(
            result.Issues,
            issue => issue.Kind ==
                SessionMacroAssignmentIssueKind.WrongMacroKind);
        Assert.Contains(
            result.Issues,
            issue => issue.Kind ==
                SessionMacroAssignmentIssueKind.AmbiguousMacro);
    }

    private static SessionTemplate PerClientTemplate(
        params (string AccountKey, string MacroId)[] assignments) =>
        new()
        {
            MacroMode = SessionTemplateMacroMode.PerClient,
            ClientSlots = assignments
                .Select((assignment, index) => new SessionTemplateClientSlot
                {
                    SlotId = $"slot_{index}",
                    AccountKey = assignment.AccountKey,
                    Order = index,
                    PerClientMacroId = assignment.MacroId
                })
                .ToList()
        };

    private static SessionTemplateClientSlot Slot(
        string accountKey,
        int order) =>
        new()
        {
            SlotId = $"slot_{order}",
            AccountKey = accountKey,
            Order = order
        };

    private static SessionTemplateCatalog Catalog(
        params MacroDefinition[] definitions) =>
        new()
        {
            MacroDefinitions = [.. definitions]
        };

    private static MacroDefinition Definition(
        string contentId,
        SessionMacroKind kind) =>
        new()
        {
            ContentId = contentId,
            SafeFileName = contentId + ".ewmacro",
            Name = contentId,
            Kind = kind,
            Sha256 = new string('A', 64)
        };

    private static ExactWheelMacroStore CreateStore(string root) =>
        new(new SessionTemplateStore(root));

    private static string MacroPath(
        string root,
        MacroDefinition definition) =>
        Path.Combine(root, "Macros", definition.SafeFileName);

    private static void AssertUnavailable(
        SessionTemplateMacroPreflightResult result,
        MacroDefinition definition)
    {
        Assert.False(result.Success);
        Assert.Equal(
            SessionTemplateMacroPreflightFailureKind.MacroUnavailable,
            result.FailureKind);
        Assert.Equal(definition.ContentId, result.MacroId);
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "SessionDock.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "The SessionDock repository root could not be located.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SessionDock.MacroPreflight.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
