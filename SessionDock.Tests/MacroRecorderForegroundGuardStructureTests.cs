namespace SessionDock.Tests;

public sealed class MacroRecorderForegroundGuardStructureTests
{
    [Fact]
    public void ClientStopNativeHook_DoesNotActivateOrStopBeforeWpfClick()
    {
        var source = ReadRecorderSource();
        var mouseActivation = Slice(
            source,
            "if (message != WindowMessageMouseActivate ||",
            "internal static nint GetStopButtonMouseActivationResult(");

        Assert.Contains("handled = true", mouseActivation);
        Assert.Contains("return mouseActivationResult", mouseActivation);
        Assert.DoesNotContain("StopRecordingAsync", mouseActivation);
        Assert.DoesNotContain("StopRecordingCapture", mouseActivation);
        Assert.DoesNotContain("Dispatcher.BeginInvoke", mouseActivation);
        Assert.DoesNotContain("DialogResult", mouseActivation);
        Assert.Contains("StopButton.Focusable = !clientCaptureActive", source);
        Assert.Contains("StopButton.IsTabStop = !clientCaptureActive", source);
        Assert.Contains("StopActivationKind.Keyboard", source);
        Assert.Contains("RemoveTerminalKeyboardStopInteraction", source);
        Assert.DoesNotContain("removePendingStopPress", source);
    }

    [Fact]
    public void ClientRecording_UsesEventAdmissionWithoutForegroundLossAbort()
    {
        var source = ReadRecorderSource();
        var start = Slice(
            source,
            "var macroKind = WholeLayoutModeRadioButton.IsChecked == true",
            "catch (OperationCanceledException)");

        Assert.Contains("if (macroKind == SessionMacroKind.Client)", start);
        Assert.Contains("AcquirePlaybackTargetLease", start);
        Assert.Contains("EventAdmission = eventAdmission", start);
        Assert.Contains("ClientRecordingAdmissionPolicy", start);
        Assert.Contains("targetLease.GetDispatchAuthorization", start);
        Assert.Contains(
            "eventAdmission = _recordingAdmissionPolicy.TryAdmit",
            start);
        Assert.DoesNotContain("ClientForegroundMonitor", source);
        Assert.DoesNotContain("AbortClientRecordingForForegroundLoss", source);
        Assert.DoesNotContain("DiscardForForegroundLoss", source);
        Assert.Contains(
            "MacroKind == SessionMacroKind.WholeLayout",
            source);
    }

    [Fact]
    public void IntentionalStop_ClaimsCaptureBeforeReleasingTargetLease()
    {
        var source = ReadRecorderSource();
        var stopClick = Slice(
            source,
            "private async void StopButton_Click(",
            "private Task StopRecordingAsync(");
        var stopCapture = Slice(
            source,
            "private StoppedRecordingCapture? StopRecordingCapture(",
            "private async Task FinishStoppedRecordingAsync(");

        Assert.Contains("await StopRecordingAsync(activation)", stopClick);
        AssertInOrder(
            stopCapture,
            "_state = RecorderState.Stopping",
            "captured = _session.StopRecording()",
            "_recordingAdmissionPolicy.Complete(",
            "var admissionFailure = _recordingAdmissionPolicy?.Failure",
            "var targetLeaseFailure = _recordingTargetLease?.Failure",
            "DisposeRecordingTargetLease()");
    }

    [Fact]
    public void ClientAdmission_RequiresClientBoundsAndAuthorizedExactLease()
    {
        var admission = ReadAdmissionPolicySource();

        Assert.Contains("inputEvent.IsMouseEvent", admission);
        Assert.Contains("!_clientRect.Contains", admission);
        Assert.Contains(
            "authorization != ExactWheelDispatchAuthorization.Authorized",
            admission);
        Assert.Contains("_heldKeyboardKeys.Remove", admission);
        Assert.Contains("_heldMouseButtons.Remove", admission);
        Assert.Contains("internal void Complete(", admission);
    }

    [Fact]
    public void GlobalStopHotkey_ClaimsCaptureWithoutActivatingRecorderWindow()
    {
        var source = ReadRecorderSource();
        var hook = Slice(
            source,
            "private nint WindowMessageHook(",
            "internal static nint GetStopButtonMouseActivationResult(");

        Assert.Contains("WindowMessageHotkey", hook);
        Assert.Contains("StopActivationKind.GlobalHotkey", hook);
        Assert.Contains("StopRecordingCapture", hook);
        Assert.DoesNotContain("Focus()", hook);
        Assert.Contains("RegisterRecordingStopHotkey()", source);
        Assert.Contains("DisposeStopHotkeyRegistration()", source);
        Assert.Contains("RemoveTerminalGlobalHotkeyInteraction", source);
    }

    [Fact]
    public void EmptySanitizedRecording_ShowsLocalizedErrorBeforeSuccess()
    {
        var source = ReadRecorderSource();
        var finish = Slice(
            source,
            "private async Task FinishStoppedRecordingAsync(",
            "private async void CancelButton_Click(");

        AssertInOrder(
            finish,
            "var recording = SanitizeControlInteraction(",
            "if (recording.Events.Count == 0)",
            "Localize(\"Macro.EmptyRecordingError\")",
            "return;",
            "ClientRecordingAdmissionPolicy.HasBalancedTransitions(",
            "Recording = recording",
            "DialogResult = true");
    }

    private static string ReadRecorderSource() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "SessionDock",
        "MacroRecorderDialog.xaml.cs"));

    private static string ReadAdmissionPolicySource() => File.ReadAllText(
        Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Services",
            "ClientRecordingAdmissionPolicy.cs"));

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find source marker: {start}");
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find source marker: {end}");
        return source[startIndex..endIndex];
    }

    private static void AssertInOrder(string source, params string[] values)
    {
        var offset = 0;
        foreach (var value in values)
        {
            var index = source.IndexOf(value, offset, StringComparison.Ordinal);
            Assert.True(
                index >= 0,
                $"Could not find ordered source marker: {value}");
            offset = index + value.Length;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SessionDock.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
