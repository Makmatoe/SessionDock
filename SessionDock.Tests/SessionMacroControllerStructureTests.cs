using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class SessionMacroControllerStructureTests
{
    [Fact]
    public void FloatingController_ContainsOnlyPlayAndSpeedControls()
    {
        var document = XDocument.Load(RepoFile(
            "SessionDock",
            "SessionMacroControllerWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Single(document.Descendants(presentation + "Button"));
        Assert.Single(document.Descendants(presentation + "ComboBox"));
        Assert.Empty(document.Descendants(presentation + "CheckBox"));
        Assert.Empty(document.Descendants(presentation + "TextBox"));
        Assert.Equal(
            "Height",
            document.Root!.Attribute("SizeToContent")?.Value);
        Assert.Null(document.Root.Attribute("Height"));
        Assert.Null(document.Root.Attribute("MaxHeight"));
        Assert.Equal(
            "True",
            document.Root!.Attribute("Topmost")?.Value);
        Assert.Equal(
            "False",
            document.Root.Attribute("ShowActivated")?.Value);

        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "SessionMacroControllerWindow.xaml.cs"));
        var reopenStart = source.IndexOf(
            "internal void Reopen(bool userInitiated)",
            StringComparison.Ordinal);
        var reopenEnd = source.IndexOf(
            "internal void ClosePermanently()",
            reopenStart,
            StringComparison.Ordinal);
        var reopen = source[reopenStart..reopenEnd];
        Assert.Contains("if (userInitiated)", reopen);
        Assert.Contains("Activate()", reopen);
        Assert.Contains("PlayButton.Focus()", reopen);
        Assert.Contains("_playbackCancellation?.Cancel()", source);
        Assert.Contains("playbackCancellation.Token", source);
        Assert.Contains("Localize(\"Macro.Stop\")", source);
        Assert.Contains("var playEnabled = _isPlaying", source);
        Assert.Contains("PlayButton.IsEnabled = playEnabled", source);
        Assert.DoesNotContain("CancellationToken.None", source);

        var hostSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.SessionMacros.cs"));
        Assert.Contains(
            "OpenMacroController(userInitiated: false)",
            hostSource);
        Assert.Contains(
            "OpenMacroController(userInitiated: true)",
            hostSource);
    }

    [Fact]
    public void PostLaunch_DoesNotContainCountdownOrAutomaticPlayback()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.Templates.cs"));
        var postLaunchStart = source.IndexOf(
            "ApplySessionPostLaunchAsync",
            StringComparison.Ordinal);
        var playbackStart = source.IndexOf(
            "PlayTemplateMacrosAsync",
            postLaunchStart,
            StringComparison.Ordinal);
        var postLaunch = source[postLaunchStart..playbackStart];

        Assert.DoesNotContain("TimeSpan.FromSeconds(3)", source);
        Assert.DoesNotContain("PlaybackCountdown", source);
        Assert.DoesNotContain("PlayTemplateMacrosAsync(", postLaunch);
        Assert.Contains("InstallCurrentBatchMacroContext", postLaunch);
    }

    [Fact]
    public void ExplicitPlay_UsesTransferredCacheAndLoadsOnlyNewSources()
    {
        var controllerSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.SessionMacros.cs"));
        var playbackSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.Templates.cs"));

        Assert.Contains("PrepareRuntimeMacroPlan", controllerSource);
        Assert.Contains("store.Load(candidate)", controllerSource);
        Assert.Contains("ExactWheelPlaybackRate.Parse", controllerSource);
        Assert.Contains("Rate = playbackRate", playbackSource);
        Assert.Equal(
            2,
            CountOccurrences(
                playbackSource,
                "playbackLeases.GetOrAcquire("));
        Assert.Equal(
            2,
            CountOccurrences(
                playbackSource,
                "FocusAsync("));
        Assert.Equal(
            2,
            CountOccurrences(
                playbackSource,
                ".WaitForFocusTransitionAsync("));
        Assert.Equal(
            2,
            CountOccurrences(
                playbackSource,
                "canProgrammaticallyActivate:"));
        var normalizedPlaybackSource = playbackSource.ReplaceLineEndings("\n");
        var perClientStart = normalizedPlaybackSource.IndexOf(
            "private async Task<TemplateMacroPlaybackResult>\n" +
                "        PlayPerClientMacrosAsync(",
            StringComparison.Ordinal);
        var wholeLayoutStart = normalizedPlaybackSource.IndexOf(
            "private async Task<TemplateMacroPlaybackResult>\n" +
                "        PlayWholeLayoutMacroAsync(",
            StringComparison.Ordinal);
        Assert.True(perClientStart >= 0);
        Assert.True(wholeLayoutStart > perClientStart);
        var perClientMethod = normalizedPlaybackSource[
            perClientStart..wholeLayoutStart];
        var wholeLayoutMethod = normalizedPlaybackSource[wholeLayoutStart..];
        Assert.True(
            perClientMethod.IndexOf(
                ".WaitForFocusTransitionAsync(",
                StringComparison.Ordinal) <
            perClientMethod.IndexOf("FocusAsync(", StringComparison.Ordinal));
        Assert.True(
            wholeLayoutMethod.IndexOf(
                ".WaitForFocusTransitionAsync(",
                StringComparison.Ordinal) <
            wholeLayoutMethod.IndexOf("FocusAsync(", StringComparison.Ordinal));
        Assert.Contains("playbackLease,", playbackSource);
        Assert.DoesNotContain("using var playbackLease", playbackSource);
        Assert.Contains(
            "prepared.PlaybackLeases.Dispose()",
            controllerSource);
        Assert.DoesNotContain(
            "playbackLease.IsDispatchAuthorized",
            playbackSource);
        Assert.Contains("pauseOnFocusLoss: true", playbackSource);
        Assert.Contains(
            "EventDispatchAuthorization = inputEvent =>",
            playbackSource);
        Assert.Contains(
            ".GetDispatchAuthorization(inputEvent)",
            playbackSource);
        Assert.DoesNotContain(
            "ExactWheelDesktopCapture.IsForeground",
            playbackSource);
    }

    [Fact]
    public void RetiredClientMode_DoesNotSuppressPreparedWholeMacro()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.SessionMacros.cs"));

        Assert.Contains(
            "var clientModeActive = prepared.ClientTemplate is not null;",
            source);
        Assert.Contains(
            "if (wholeModeActive &&\n" +
                "                            prepared.WholeTemplate is not null)",
            source.ReplaceLineEndings("\n"));
        Assert.DoesNotContain(
            "warnings.Count == 0 && prepared.WholeTemplate is not null",
            source);
    }

    [Fact]
    public void PostLaunch_WholeMacroRequiresCompleteWindowSetAndLayout()
    {
        var postLaunchSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.Templates.cs"));
        var plannerSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "Services",
            "SessionMacroLaunchContext.cs"));

        Assert.Contains(
            "wholeLayoutCompletedSuccessfully:",
            postLaunchSource);
        Assert.Contains(
            "layout is { Success: true }",
            postLaunchSource);
        Assert.Contains("HasCompleteUniqueClientSet", plannerSource);
        Assert.Contains("WholeSessionNotReady", plannerSource);
    }

    [Fact]
    public void PerClientCapture_Win32FailureSkipsOnlyThatAssignment()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.Templates.cs"));
        var firstCall = source.IndexOf(
            "PlayPerClientMacrosAsync(",
            StringComparison.Ordinal);
        var secondCall = source.IndexOf(
            "PlayPerClientMacrosAsync(",
            firstCall + 1,
            StringComparison.Ordinal);
        var start = source.IndexOf(
            "PlayPerClientMacrosAsync(",
            secondCall + 1,
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "internal static IReadOnlyList<SessionTemplateClientSlot>",
            start,
            StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains(
            "exception is System.ComponentModel.Win32Exception",
            method);
        Assert.Contains(
            "playbackRetryTracker.ReportFailure(",
            method);
        Assert.Contains(
            "SessionMacroPlaybackRetryDisposition.Transient",
            method);
        Assert.Contains("skipped++;", method);
        Assert.Contains("continue;", method);
    }

    [Fact]
    public void FloatingController_UsesAdvisoryReadinessAndAuthoritativePlay()
    {
        var controllerSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "SessionMacroControllerWindow.xaml.cs"));
        var runtimeSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.SessionMacros.cs"));
        var leaseCacheSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "Services",
            "SessionMacroPlaybackLeaseCache.cs"));
        var readinessStart = runtimeSource.IndexOf(
            "private SessionMacroControllerReadiness PrepareMacroControllerReadiness(",
            StringComparison.Ordinal);
        var readinessEnd = runtimeSource.IndexOf(
            "private void PersistMacroPlaybackSpeed(",
            readinessStart,
            StringComparison.Ordinal);
        var readiness = runtimeSource[readinessStart..readinessEnd];

        Assert.Contains("_prepareReadiness", controllerSource);
        Assert.Contains("_readinessTimer", controllerSource);
        Assert.Contains(
            "_readiness = EvaluateReadiness(snapshot);",
            controllerSource);
        Assert.Contains(": _readiness.CanPlay;", controllerSource);
        Assert.Contains("PrepareMacroControllerReadiness", runtimeSource);
        Assert.Contains("PrepareRuntimeMacroPlan(", runtimeSource);
        Assert.Contains("validateMacroArtifacts: false", readiness);
        Assert.DoesNotContain("AcquirePlaybackTargetLease", readiness);
        Assert.Contains("AcquirePlaybackTargetLease", leaseCacheSource);
    }

    [Fact]
    public void NewBatch_CancelsAndAwaitsPlaybackBeforeClosingClients()
    {
        var batchSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.Batch.cs"));
        var stop = batchSource.IndexOf(
            "await CancelAndWaitForCurrentMacroPlaybackAsync(cancellationToken)",
            StringComparison.Ordinal);
        var preflight = batchSource.IndexOf(
            "await PreflightBatchAccountsAsync(",
            StringComparison.Ordinal);
        var preflightFailure = batchSource.IndexOf(
            "if (preflight.Failures.Count > 0)",
            StringComparison.Ordinal);
        var close = batchSource.IndexOf(
            "CloseAllPlayersAsync(",
            StringComparison.Ordinal);

        Assert.True(preflight >= 0);
        Assert.True(preflightFailure > preflight);
        Assert.True(stop > preflightFailure);
        Assert.True(stop >= 0);
        Assert.True(close > stop);

        var controllerSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.SessionMacros.cs"));
        Assert.Contains("_macroPlaybackCompletion", controllerSource);
        Assert.Contains("playbackCompletion.TrySetResult()", controllerSource);
    }

    [Fact]
    public void CurrentBatchAssignments_AreLockedForTheDurationOfPlayback()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.SessionMacros.cs"));

        Assert.Contains(
            "_macroPlaybackInProgress ||",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var canAssign = canInteract && !_macroPlaybackInProgress;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var context = _currentMacroContext;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "!ReferenceEquals(context, _currentMacroContext)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (_macroAssignmentInProgress)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_macroPlaybackInProgress = true;" + Environment.NewLine +
            "        UpdateCurrentMacroActions();",
            source.ReplaceLineEndings(),
            StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(source, "UpdateCurrentMacroActions();") >= 7);
    }

    private static string RepoFile(params string[] components)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null &&
               !File.Exists(Path.Combine(current.FullName, "SessionDock.slnx")))
        {
            current = current.Parent;
        }
        if (current is null)
            throw new DirectoryNotFoundException("Repository root not found.");
        return Path.Combine([current.FullName, .. components]);
    }

    private static int CountOccurrences(string source, string value) =>
        (source.Length - source.Replace(
            value,
            string.Empty,
            StringComparison.Ordinal).Length) / value.Length;
}
