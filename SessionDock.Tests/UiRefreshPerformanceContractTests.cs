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
        Assert.Contains("foreach (var client in _selectableClients)", tick);
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
        Assert.Contains("ObserveLateSuspension(", webSession);
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
