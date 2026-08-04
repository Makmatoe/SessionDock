namespace SessionDock.Tests;

public sealed class UiRefreshPerformanceContractTests
{
    [Fact]
    public void MacroController_CoalescesAndAdaptsIdleReadinessWork()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "SessionMacroControllerWindow.xaml.cs"));

        Assert.Contains("TimeSpan.FromSeconds(15)", source);
        Assert.Contains("TimeSpan.FromSeconds(30)", source);
        Assert.Contains("DispatcherPriority.ApplicationIdle", source);
        Assert.Contains("_presentationRefreshQueued", source);
        Assert.Contains("_queuedReadinessEvaluation |= evaluateReadiness", source);
        Assert.Contains("previous == _readiness", source);
        Assert.Contains("_readinessTimer.Stop();", source);
        Assert.Contains("ControllerPresentation? _renderedPresentation", source);

        var tick = Method(source, "private void ReadinessTimer_Tick", "private void StartReadinessTimer");
        Assert.Contains("if (_isPlaying || !IsVisible)", tick);
        Assert.Contains("IdleReadinessInterval", tick);
    }

    [Fact]
    public void ClientAssignmentPolling_ReusesImmutableClientsAndOnlyRunsWhenNeeded()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "ClientMacroAssignmentDialog.xaml.cs"));
        var tick = Method(
            source,
            "private async void ForegroundTimer_Tick",
            "private void RemoveAssignmentButton_Click");

        Assert.Contains("TimeSpan.FromMilliseconds(250)", source);
        Assert.Contains("_selectableClients = _context.Snapshot().Clients", source);
        Assert.Contains(
            "_selectableClientsByWindowHandle = _selectableClients",
            source);
        Assert.Contains(
            "ExactWheelDesktopCapture.GetForegroundRootWindow()",
            tick);
        Assert.Contains(
            "_selectableClientsByWindowHandle.TryGetValue(",
            tick);
        Assert.Equal(
            2,
            CountOccurrences(
                tick,
                "ExactWheelDesktopCapture.GetForegroundRootWindow()"));
        Assert.DoesNotContain("foreach (var client in _selectableClients)", tick);
        Assert.DoesNotContain("ExactWheelDesktopCapture.IsForeground", tick);
        Assert.DoesNotContain("_context.Snapshot()", tick);
        Assert.DoesNotContain(".ToArray()", tick);
        Assert.Contains("UpdateForegroundPolling()", source);
        Assert.Contains("if (!_foregroundTimer.IsEnabled)", source);
    }

    [Fact]
    public void RepeatedStatusRendering_DoesNotReapplyUnchangedVisualProperties()
    {
        var mainWindow = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.xaml.cs"));
        var liveRegion = File.ReadAllText(RepoFile(
            "SessionDock",
            "Services",
            "AccessibilityLiveRegion.cs"));

        Assert.Contains("if (_lastStatusTone == tone)", mainWindow);
        Assert.Contains("StatusDetail.Text, detail", mainWindow);
        Assert.Contains("SessionBadge.Text, badge", mainWindow);
        Assert.Contains("_lastStatusAnnouncement", mainWindow);
        Assert.Contains("_target.Text, displayText", liveRegion);
        Assert.Contains("AutomationProperties.GetName(_target)", liveRegion);
        Assert.Contains("AutomationProperties.GetLiveSetting(_target)", liveRegion);
    }

    [Fact]
    public void MacroPerformanceMode_SuspendsOnlyAnIdleHiddenWebSession()
    {
        var mainWindow = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.xaml.cs"));
        var webSession = File.ReadAllText(RepoFile(
            "SessionDock",
            "Services",
            "RobloxWebSessionService.cs"));
        var helper = Method(
            mainWindow,
            "TryEnterMacroPlaybackPerformanceModeAsync(",
            "private bool TryGetAffineWebSessionToken");

        Assert.Contains("_macroPlaybackInProgress", helper);
        Assert.Contains("_operationBusy", helper);
        Assert.Contains("_launchInProgress", helper);
        Assert.Contains("_macroAssignmentInProgress", helper);
        Assert.Contains("BrowserPanel.Visibility != Visibility.Collapsed", helper);
        Assert.Contains("IsAutoJoinWatchActive", helper);
        Assert.Contains("_accountCheckLock.CurrentCount == 0", helper);
        Assert.Contains("CancellationToken cancellationToken", helper);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", helper);
        Assert.Contains("cancellationToken)", helper);
    }

    [Fact]
    public void MacroPerformanceSuspension_IsBoundedAndObservesLateCompletion()
    {
        var webSession = File.ReadAllText(RepoFile(
            "SessionDock",
            "Services",
            "RobloxWebSessionService.cs"));

        Assert.Contains("core.TrySuspendAsync()", webSession);
        Assert.Contains("MacroPlaybackSuspensionTimeout", webSession);
        Assert.Contains("suspensionTask.WaitAsync(", webSession);
        Assert.Contains("catch (TimeoutException)", webSession);
        Assert.Contains("catch (OperationCanceledException)", webSession);
        Assert.Contains("CreatePendingSuspensionLease(", webSession);
        Assert.Contains("return pendingLease;", webSession);
        Assert.Contains("pendingLease.Dispose();", webSession);
        Assert.Contains("_browserWorkGeneration", webSession);
        Assert.Contains("RevokePendingMacroSuspension()", webSession);
        Assert.Contains("ResumeOnDispatcherAsync", webSession);
        Assert.Contains("DispatcherPriority.Send", webSession);
        Assert.Contains("_macroSuspensionGate", webSession);
        Assert.Contains("CoreWebView2? ownedSuspendedCore = null", webSession);
        Assert.Contains("ResumeSafely(ownedSuspendedCore)", webSession);
        Assert.Contains("WebSessionSuspensionLease", webSession);
        Assert.Contains("Interlocked.Exchange(ref _state, null)", webSession);
        Assert.Contains("state.SuspensionGate.Release()", webSession);
        Assert.Contains("if (core.IsSuspended)", webSession);
        Assert.Contains("core.Resume()", webSession);
    }

    [Fact]
    public void MacroPlayback_ReusesOneSessionAndBoundsTransientUiProgress()
    {
        var host = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.SessionMacros.cs"));
        var playback = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.Templates.cs"));

        Assert.Contains(
            "await using var playbackSession = new ExactWheelSession()",
            host);
        Assert.Contains("Task.Run(", host);
        Assert.Contains("RunMacroPlaybackCoreAsync(", host);
        Assert.Contains("CancellationToken.None", host);
        var playEntry = Method(
            host,
            "private async Task<SessionMacroPlaybackOutcome>",
            "private async Task RunMacroPlaybackCoreAsync(");
        var backgroundStart = playEntry.IndexOf(
            "prepared = await Task.Run(",
            StringComparison.Ordinal);
        var authoritativePreparation = playEntry.IndexOf(
            "PrepareRuntimeMacroPlan(",
            StringComparison.Ordinal);
        Assert.True(backgroundStart >= 0);
        Assert.True(authoritativePreparation > backgroundStart);
        var preparation = Method(
            host,
            "private RuntimeMacroPlan PrepareRuntimeMacroPlan(",
            "private SessionMacroControllerReadiness PrepareMacroControllerReadiness(");
        Assert.DoesNotContain("Localize(", preparation);
        Assert.DoesNotContain("Dispatcher", preparation);
        Assert.DoesNotContain("SetStatus(", preparation);
        Assert.Contains(
            "bool validateMacroArtifacts = true,",
            preparation);
        Assert.Contains(
            "cancellationToken.ThrowIfCancellationRequested();",
            preparation);
        var core = Method(
            host,
            "private async Task RunMacroPlaybackCoreAsync(",
            "private MacroPlaybackText CaptureMacroPlaybackText()");
        Assert.DoesNotContain("Localize(", core);
        Assert.Contains("playbackText", core);
        Assert.Equal(
            1,
            CountOccurrences(
                string.Concat(host, playback),
                "new ExactWheelSession()"));
        Assert.Contains("playbackSession", playback);
        Assert.DoesNotContain(
            "await using var session = new ExactWheelSession()",
            playback);
        Assert.Contains("ReportMacroPlaybackProgress", playback);
        Assert.Contains("_macroPlaybackProgressThrottle.TryAcquire()", playback);
        Assert.Contains("Dispatcher.BeginInvoke(", playback);
        Assert.Contains("dispatch.PostPending", playback);
        Assert.Contains("announceChanges: false", playback);
        Assert.Contains("plan.ClientPlaybackSlots", playback);
        Assert.Contains("plan.ProcessBasenamesByKey", playback);
        Assert.DoesNotContain("Path.GetFileName", playback);
        Assert.Contains(
            "static (store, candidate) => store.Load(candidate)",
            string.Concat(host, playback));
        Assert.Contains("GetOrLoadAndCreateTransform(", playback);
        Assert.Contains("CoordinateTransform = coordinateTransform", playback);
        Assert.DoesNotContain(
            "var source = playbackCache.GetOrLoad(",
            playback);
        Assert.Contains("static (recording, target)", playback);
        Assert.DoesNotContain("() => ExactWheelCoordinateTransforms", playback);
        Assert.Contains("GetOrCaptureWindowClass", playback);
        Assert.Contains("focused.Window!.OuterBounds", playback);
        Assert.Contains("focused.Window.ClientBounds", playback);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(
                   value,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Method(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing method boundary: {endMarker}");
        return source[start..end];
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
}
