using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

internal sealed class MacroRecorderTargetOption : IDropdownLabel
{
    internal MacroRecorderTargetOption(
        AttributedRunningClient client,
        RobloxWindowSnapshot verifiedWindow)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        VerifiedWindow = verifiedWindow ??
            throw new ArgumentNullException(nameof(verifiedWindow));
        if (verifiedWindow.Handle == nint.Zero ||
            !RobloxClientProcessIdentityComparer.Instance.Equals(
                client.Identity,
                verifiedWindow.Identity))
        {
            throw new ArgumentException(
                "The attributed client and verified window do not share an identity.",
                nameof(verifiedWindow));
        }

        var attribution = client.Attribution;
        AccountKey = attribution.AccountKey;
        DisplayName = !string.IsNullOrWhiteSpace(attribution.AccountLabel)
            ? $"{attribution.AccountLabel} ({attribution.AccountUsername})"
            : !string.IsNullOrWhiteSpace(attribution.AccountUsername)
                ? attribution.AccountUsername
                : attribution.AccountKey;
    }

    internal AttributedRunningClient Client { get; }

    internal RobloxWindowSnapshot VerifiedWindow { get; }

    internal string AccountKey { get; }

    public string DisplayName { get; }
}

public partial class MacroRecorderDialog : Window
{
    private const int CountdownSeconds = 3;
    private const int VirtualKeyLeftMouseButton = 0x01;
    private const int WindowMessageMouseActivate = 0x0021;
    private const int WindowMessageLeftButtonDown = 0x0201;
    private const int WindowMessageHotkey = 0x0312;
    private const int MouseActivateNoActivate = 3;
    private const int RecordingStopHotkeyIdentifier = 0x5344;
    private const int MaximumGlobalHotkeyTailEvents = 64;
    private const ulong MaximumGlobalHotkeyTailMicroseconds = 2_000_000;

    private readonly IReadOnlyList<MacroRecorderTargetOption> _targets;
    private readonly RobloxWindowService _windowService;
    private readonly AppLocalizationService _localization;
    private readonly MacroRecordingHotkey _recordingStopHotkey;
    private readonly ExactWheelSession _session = new();
    private readonly AccessibilityLiveRegion _statusLiveRegion;
    private CancellationTokenSource? _countdownCancellation;
    private ClientRecordingAdmissionPolicy? _recordingAdmissionPolicy;
    private RobloxPlaybackTargetLease? _recordingTargetLease;
    private GlobalRecordingHotkeyRegistration? _stopHotkeyRegistration;
    private HwndSource? _windowSource;
    private ExactWheelRecordingTarget? _recordingTarget;
    private StopActivationKind _pendingStopActivation;
    private RecorderState _state;
    private bool _sessionDisposed;
    private bool _closing;

    internal MacroRecorderDialog(
        IReadOnlyList<MacroRecorderTargetOption> verifiedTargets,
        RobloxWindowService windowService,
        string? recordingStopHotkey = null)
    {
        ArgumentNullException.ThrowIfNull(verifiedTargets);
        _windowService = windowService ??
            throw new ArgumentNullException(nameof(windowService));
        _targets = verifiedTargets
            .Where(target => target is not null)
            .GroupBy(
                target => target.Client.Identity,
                RobloxClientProcessIdentityComparer.Instance)
            .Select(group => group.First())
            .OrderBy(target => target.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        InitializeComponent();
        _localization = ((App)Application.Current).LocalizationService;
        _ = MacroRecordingHotkeyPolicy.TryParse(
            MacroRecordingHotkeyPolicy.Normalize(recordingStopHotkey),
            out var parsedStopHotkey);
        _recordingStopHotkey = parsedStopHotkey;
        _statusLiveRegion = new AccessibilityLiveRegion(StatusText);
        WindowLayoutService.FitToWorkArea(this);
        TargetComboBox.ItemsSource = _targets;
        if (_targets.Count > 0)
            TargetComboBox.SelectedIndex = 0;
        UpdateControls();
    }

    internal ExactWheelRecording? Recording { get; private set; }

    internal string MacroName { get; private set; } = string.Empty;

    internal SessionMacroKind MacroKind { get; private set; }

    internal string? RecordedAccountKey { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        NameTextBox.Focus();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_state != RecorderState.Idle)
            return;
        var name = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus(Localize("Macro.NameRequired"), isError: true);
            NameTextBox.Focus();
            return;
        }
        if (TargetComboBox.SelectedItem is not MacroRecorderTargetOption target)
        {
            SetStatus(Localize("Macro.TargetRequired"), isError: true);
            TargetComboBox.Focus();
            return;
        }

        _state = RecorderState.Countdown;
        _countdownCancellation?.Dispose();
        _countdownCancellation = new CancellationTokenSource();
        var cancellationToken = _countdownCancellation.Token;
        UpdateControls();
        try
        {
            for (var remaining = CountdownSeconds; remaining > 0; remaining--)
            {
                SetStatus(Localize("Macro.Countdown", remaining));
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            SetStatus(Localize("Macro.Focusing"));
            var focused = await _windowService.FocusAsync(
                target.Client.Identity,
                target.VerifiedWindow.Handle,
                cancellationToken: cancellationToken);
            if (!focused.Success || focused.Window is null)
            {
                throw new InvalidOperationException(
                    Localize(
                        "Macro.FocusFailed",
                        focused.Error ?? focused.Status.ToString()));
            }

            var macroKind = WholeLayoutModeRadioButton.IsChecked == true
                ? SessionMacroKind.WholeLayout
                : SessionMacroKind.Client;
            _recordingTarget = ExactWheelDesktopCapture.CaptureRecordingTarget(
                focused.Window.Handle,
                requireForeground: true);
            Func<ExactWheelInputEvent, bool>? eventAdmission = null;
            if (macroKind == SessionMacroKind.Client)
            {
                var acquisition = _windowService.AcquirePlaybackTargetLease(
                    target.Client.Identity,
                    _recordingTarget.WindowHandle);
                var targetLease = acquisition.Lease;
                if (!acquisition.Success || targetLease is null)
                {
                    throw new InvalidOperationException(
                        acquisition.Failure?.Error ??
                        "The selected client could not be retained for safe recording.");
                }

                _recordingTargetLease = targetLease;
                _recordingAdmissionPolicy = new ClientRecordingAdmissionPolicy(
                    _recordingTarget.Metadata.ClientRect,
                    targetLease.GetDispatchAuthorization);
                eventAdmission = _recordingAdmissionPolicy.TryAdmit;
            }

            RegisterRecordingStopHotkey();
            _session.StartRecording(
                _recordingTarget,
                new ExactWheelRecordingOptions
                {
                    ArmUntilReleasedVirtualKeys =
                    [
                        VirtualKeyLeftMouseButton,
                        _recordingStopHotkey.VirtualKey,
                        .. _recordingStopHotkey.ModifierVirtualKeys
                    ],
                    EventAdmission = eventAdmission
                });
            MacroName = name;
            MacroKind = macroKind;
            RecordedAccountKey = MacroKind == SessionMacroKind.Client
                ? target.AccountKey
                : null;
            _pendingStopActivation = StopActivationKind.Unknown;
            _state = RecorderState.Recording;
            SetStatus(Localize(
                "Macro.RecordingWithHotkey",
                _recordingStopHotkey.DisplayName));
            UpdateControls();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_closing)
            {
                _state = RecorderState.Idle;
                DisposeStopHotkeyRegistration();
                DisposeRecordingTargetLease();
                SetStatus(Localize("Macro.StatusReady"));
                UpdateControls();
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or
                Win32Exception)
        {
            _state = RecorderState.Idle;
            DisposeStopHotkeyRegistration();
            DisposeRecordingTargetLease();
            _recordingTarget = null;
            SetStatus(Localize("Macro.Error", exception.Message), isError: true);
            UpdateControls();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var activation = _pendingStopActivation;
        _pendingStopActivation = StopActivationKind.Unknown;
        if (activation == StopActivationKind.Unknown)
        {
            activation = InputManager.Current.MostRecentInputDevice switch
            {
                MouseDevice => StopActivationKind.Mouse,
                KeyboardDevice => StopActivationKind.Keyboard,
                _ => StopActivationKind.Unknown
            };
        }

        await StopRecordingAsync(activation);
    }

    private Task StopRecordingAsync(StopActivationKind stopActivation)
    {
        var stopped = StopRecordingCapture(stopActivation);
        if (stopped is null)
            return Task.CompletedTask;

        return FinishStoppedRecordingAsync(stopped);
    }

    private StoppedRecordingCapture? StopRecordingCapture(
        StopActivationKind stopActivation)
    {
        if (_state != RecorderState.Recording || _recordingTarget is null)
            return null;

        var recordingTarget = _recordingTarget;
        var macroKind = MacroKind;
        var stopButtonBounds = GetStopButtonScreenBounds();
        _state = RecorderState.Stopping;
        DisposeStopHotkeyRegistration();
        ExactWheelRecording? captured = null;
        Exception? failure = null;
        try
        {
            captured = _session.StopRecording();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException or
                Win32Exception)
        {
            failure = exception;
        }

        if (failure is null && _recordingAdmissionPolicy is not null)
        {
            var terminalKeys = GetAllowedTerminalKeyboardKeys(
                stopActivation,
                _recordingStopHotkey);
            _recordingAdmissionPolicy.Complete(
                terminalKeys.AllowedKeys,
                terminalKeys.RequiredKey,
                terminalKeys.MaximumKeyCount);
        }
        var admissionFailure = _recordingAdmissionPolicy?.Failure;
        var targetLeaseFailure = _recordingTargetLease?.Failure;
        DisposeRecordingTargetLease();
        if (failure is null && admissionFailure is not null)
        {
            failure = new InvalidOperationException(
                Localize(AdmissionFailureResourceKey(admissionFailure.Kind)));
        }
        else if (failure is null && targetLeaseFailure is not null)
        {
            failure = new InvalidOperationException(
                targetLeaseFailure.Error);
        }

        return new StoppedRecordingCapture(
            recordingTarget,
            macroKind,
            stopButtonBounds,
            stopActivation,
            _recordingStopHotkey,
            captured,
            failure);
    }

    private static string AdmissionFailureResourceKey(
        ClientRecordingAdmissionFailureKind kind) => kind switch
        {
            ClientRecordingAdmissionFailureKind.AuthorizationUnavailable =>
                "Macro.RecordingAdmissionAuthorizationError",
            ClientRecordingAdmissionFailureKind.FocusLostWhileInputHeld =>
                "Macro.RecordingAdmissionHeldFocusError",
            ClientRecordingAdmissionFailureKind.PointerLeftWhileButtonHeld =>
                "Macro.RecordingAdmissionHeldPointerError",
            ClientRecordingAdmissionFailureKind.InputStillHeldAtStop =>
                "Macro.RecordingAdmissionHeldAtStopError",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private async Task FinishStoppedRecordingAsync(
        StoppedRecordingCapture stopped)
    {
        SetStatus(Localize("Macro.Stopping"));
        UpdateControls();
        if (stopped.Failure is not null || stopped.Captured is null)
        {
            Recording = null;
            _recordingTarget = null;
            _state = RecorderState.Idle;
            SetStatus(
                Localize(
                    "Macro.Error",
                    stopped.Failure?.Message ?? "Recording did not return data."),
                isError: true);
            UpdateControls();
            return;
        }

        try
        {
            var recording = SanitizeControlInteraction(
                stopped.Captured,
                stopped.Target,
                stopped.Kind,
                stopped.StopButtonBounds,
                stopped.StopActivation,
                stopped.StopHotkey);
            if (recording.Events.Count == 0)
            {
                Recording = null;
                _recordingTarget = null;
                _state = RecorderState.Idle;
                SetStatus(
                    Localize("Macro.EmptyRecordingError"),
                    isError: true);
                UpdateControls();
                return;
            }
            if (stopped.Kind == SessionMacroKind.Client &&
                !ClientRecordingAdmissionPolicy.HasBalancedTransitions(
                    recording.Events))
            {
                throw new InvalidDataException(
                    "The client recording contained an incomplete key or " +
                    "mouse-button transition and was not saved.");
            }
            Recording = recording;
            _recordingTarget = null;
            _state = RecorderState.Idle;
            Topmost = false;
            await DisposeSessionAsync();
            DialogResult = true;
        }
        catch (ClientMacroOutsideTargetException)
        {
            Recording = null;
            _recordingTarget = null;
            _state = RecorderState.Idle;
            SetStatus(Localize("Macro.OutsideClientError"), isError: true);
            UpdateControls();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException or
                Win32Exception)
        {
            Recording = null;
            _recordingTarget = null;
            _state = RecorderState.Idle;
            SetStatus(Localize("Macro.Error", exception.Message), isError: true);
            UpdateControls();
        }
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await CancelAndCloseAsync();
    }

    private void StopButton_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_state == RecorderState.Recording)
            _pendingStopActivation = StopActivationKind.Mouse;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (_state != RecorderState.Recording ||
            MacroKind != SessionMacroKind.WholeLayout)
        {
            return;
        }

        if ((e.Key == Key.Enter && StopButton.IsDefault) ||
            (e.Key == Key.Space && StopButton.IsKeyboardFocusWithin))
        {
            _pendingStopActivation = StopActivationKind.Keyboard;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }

        base.OnClosed(e);
    }

    private nint WindowMessageHook(
        nint window,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        _ = window;
        if (message == WindowMessageHotkey &&
            wordParameter == new nint(RecordingStopHotkeyIdentifier))
        {
            handled = true;
            if (_state == RecorderState.Recording)
            {
                // Claim and stop the hook synchronously at WM_HOTKEY so key-up
                // events cannot leak into the saved macro or race a second stop.
                var stopped = StopRecordingCapture(
                    StopActivationKind.GlobalHotkey);
                if (stopped is not null)
                    FinishHotkeyStoppedRecording(stopped);
            }
            return nint.Zero;
        }

        _ = wordParameter;
        if (message != WindowMessageMouseActivate ||
            _state != RecorderState.Recording ||
            MacroKind != SessionMacroKind.Client ||
            _recordingTarget is null ||
            !StopButton.IsEnabled ||
            !GetCursorPos(out var cursor) ||
            !TryGetStopButtonScreenBounds(out var stopButtonBounds))
        {
            return nint.Zero;
        }

        var cursorPosition = new Point(cursor.X, cursor.Y);
        var mouseActivationResult = GetStopButtonMouseActivationResult(
            longParameter,
            stopButtonBounds,
            cursorPosition);
        if (mouseActivationResult == nint.Zero)
            return nint.Zero;

        // Keep Roblox in the foreground while still allowing Windows to
        // deliver the mouse down/up pair that raises the normal WPF Click.
        // Stopping or closing from this native hook can race the mouse-up.
        handled = true;
        return mouseActivationResult;
    }

    internal static nint GetStopButtonMouseActivationResult(
        nint mouseActivateParameter,
        Rect stopButtonBounds,
        Point cursorPosition) =>
        IsStopButtonMouseActivation(
            mouseActivateParameter,
            stopButtonBounds,
            cursorPosition)
                ? new nint(MouseActivateNoActivate)
                : nint.Zero;

    internal static bool IsStopButtonMouseActivation(
        nint mouseActivateParameter,
        Rect stopButtonBounds,
        Point cursorPosition)
    {
        var mouseMessage = (int)(
            (mouseActivateParameter.ToInt64() >> 16) & 0xffff);
        return mouseMessage == WindowMessageLeftButtonDown &&
            !stopButtonBounds.IsEmpty &&
            stopButtonBounds.Contains(cursorPosition);
    }

    private async void FinishHotkeyStoppedRecording(
        StoppedRecordingCapture stopped)
    {
        await FinishStoppedRecordingAsync(stopped);
    }

    private void RegisterRecordingStopHotkey()
    {
        DisposeStopHotkeyRegistration();
        var windowHandle = _windowSource?.Handle ?? nint.Zero;
        _stopHotkeyRegistration = GlobalRecordingHotkeyRegistration.Register(
            windowHandle,
            RecordingStopHotkeyIdentifier,
            _recordingStopHotkey);
    }

    private void DisposeStopHotkeyRegistration()
    {
        _stopHotkeyRegistration?.Dispose();
        _stopHotkeyRegistration = null;
    }

    private void DisposeRecordingTargetLease()
    {
        _recordingAdmissionPolicy = null;
        _recordingTargetLease?.Dispose();
        _recordingTargetLease = null;
    }

    private static (
        IReadOnlySet<int> AllowedKeys,
        int RequiredKey,
        int MaximumKeyCount) GetAllowedTerminalKeyboardKeys(
        StopActivationKind stopActivation,
        MacroRecordingHotkey stopHotkey)
    {
        var keys = new HashSet<int>();
        if (stopActivation != StopActivationKind.GlobalHotkey)
            return (keys, 0, 0);

        keys.UnionWith(stopHotkey.ModifierVirtualKeys);
        _ = keys.Add(stopHotkey.VirtualKey);
        var maximumKeyCount = 1;
        if (stopHotkey.Modifiers.HasFlag(
                MacroRecordingHotkeyModifiers.Control))
        {
            maximumKeyCount++;
        }
        if (stopHotkey.Modifiers.HasFlag(MacroRecordingHotkeyModifiers.Alt))
            maximumKeyCount++;
        if (stopHotkey.Modifiers.HasFlag(MacroRecordingHotkeyModifiers.Shift))
            maximumKeyCount++;
        return (keys, stopHotkey.VirtualKey, maximumKeyCount);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _countdownCancellation?.Cancel();
        EmergencyCleanupSynchronously();
        DisposeSessionSynchronously();
        base.OnClosing(e);
    }

    private async Task CancelAndCloseAsync()
    {
        if (_closing)
            return;
        _closing = true;
        _countdownCancellation?.Cancel();
        EmergencyCleanupSynchronously();
        await DisposeSessionAsync();
        DialogResult = false;
    }

    private void EmergencyCleanupSynchronously()
    {
        DisposeStopHotkeyRegistration();
        _session.EmergencyStop();
        if (_state == RecorderState.Recording)
        {
            try
            {
                _ = _session.StopRecording();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception)
            {
                // Disposal below is the final bounded hook cleanup path.
            }
        }
        DisposeRecordingTargetLease();
        _recordingTarget = null;
        _state = RecorderState.Idle;
        Topmost = false;
    }

    private async Task DisposeSessionAsync()
    {
        if (_sessionDisposed)
            return;
        _sessionDisposed = true;
        await _session.DisposeAsync();
    }

    private void DisposeSessionSynchronously()
    {
        if (_sessionDisposed)
            return;
        _sessionDisposed = true;
        _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void UpdateControls()
    {
        var idle = _state == RecorderState.Idle && !_closing;
        Topmost = !_closing && _state is
            RecorderState.Countdown or
            RecorderState.Recording or
            RecorderState.Stopping;
        NameTextBox.IsEnabled = idle;
        ClientModeRadioButton.IsEnabled = idle;
        WholeLayoutModeRadioButton.IsEnabled = idle;
        TargetComboBox.IsEnabled = idle && _targets.Count > 0;
        StartButton.IsEnabled = idle && _targets.Count > 0;
        StopButton.IsEnabled = _state == RecorderState.Recording && !_closing;
        var clientCaptureActive = !_closing &&
            MacroKind == SessionMacroKind.Client &&
            _state is RecorderState.Recording or RecorderState.Stopping;
        StopButton.Focusable = !clientCaptureActive;
        StopButton.IsTabStop = !clientCaptureActive;
        StartButton.IsDefault = idle;
        StopButton.IsDefault = _state == RecorderState.Recording &&
                               MacroKind == SessionMacroKind.WholeLayout &&
                               !_closing;
        CancelButton.IsEnabled = !_closing;
    }

    private void SetStatus(string text, bool isError = false)
    {
        _statusLiveRegion.Update(
            text,
            text,
            isError
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);
        StatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? "ErrorTextBrush" : "MutedBrush");
    }

    private Rect GetStopButtonScreenBounds() =>
        TryGetStopButtonScreenBounds(out var bounds)
            ? bounds
            : Rect.Empty;

    private bool TryGetStopButtonScreenBounds(out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!StopButton.IsVisible ||
            PresentationSource.FromVisual(StopButton) is null)
        {
            return false;
        }

        try
        {
            var topLeft = StopButton.PointToScreen(new Point(0, 0));
            var bottomRight = StopButton.PointToScreen(new Point(
                StopButton.ActualWidth,
                StopButton.ActualHeight));
            bounds = new Rect(topLeft, bottomRight);
            return !bounds.IsEmpty;
        }
        catch (InvalidOperationException)
        {
            // A native mouse message can arrive during dialog teardown. Fail
            // open instead of throwing through the HWND hook.
            return false;
        }
    }

    internal static ExactWheelRecording SanitizeControlInteraction(
        ExactWheelRecording recording,
        ExactWheelRecordingTarget target,
        SessionMacroKind kind,
        Rect stopButtonBounds,
        StopActivationKind stopActivation = StopActivationKind.Mouse,
        MacroRecordingHotkey? stopHotkey = null)
    {
        var events = recording.Events.ToList();
        switch (stopActivation)
        {
            case StopActivationKind.Mouse:
                RemoveTerminalMouseStopInteraction(events, stopButtonBounds);
                break;
            case StopActivationKind.Keyboard:
                RemoveTerminalKeyboardStopInteraction(events);
                break;
            case StopActivationKind.GlobalHotkey when stopHotkey is { } hotkey:
                RemoveTerminalGlobalHotkeyInteraction(events, hotkey);
                break;
        }

        if (kind == SessionMacroKind.Client && events.Any(inputEvent =>
                inputEvent.IsMouseEvent &&
                !target.Metadata.ClientRect.Contains(
                    inputEvent.X,
                    inputEvent.Y)))
        {
            throw new ClientMacroOutsideTargetException();
        }

        var duration = events.Count == 0
            ? 0UL
            : events[^1].TimestampMicroseconds;

        return ExactWheelRecordingValidator.Finalize(
            recording.Display,
            recording.Target,
            events,
            duration);
    }

    private static void RemoveTerminalMouseStopInteraction(
        List<ExactWheelInputEvent> events,
        Rect stopButtonBounds)
    {
        if (events.Count == 0)
            return;

        var finalStopUp = events.Count - 1;
        var stopUp = events[finalStopUp];
        if (stopUp.Type != ExactWheelInputEventType.MouseButtonUp ||
            stopUp.Data1 != (int)ExactWheelMouseButton.Left ||
            !stopButtonBounds.Contains(stopUp.X, stopUp.Y))
        {
            return;
        }

        // A Stop click is identified only when its left-button down is the
        // preceding non-move event. This avoids pairing the final mouse-up
        // with an unrelated earlier click and silently discarding input.
        var stopDown = finalStopUp - 1;
        while (stopDown >= 0 &&
               events[stopDown].Type == ExactWheelInputEventType.MouseMove)
        {
            stopDown--;
        }

        if (stopDown < 0 ||
            events[stopDown].Type != ExactWheelInputEventType.MouseButtonDown ||
            events[stopDown].Data1 != (int)ExactWheelMouseButton.Left ||
            !stopButtonBounds.Contains(events[stopDown].X, events[stopDown].Y))
        {
            return;
        }

        // The contiguous mouse-move run immediately before the click is the
        // user's final approach to the Stop button. A non-move event is the
        // deterministic boundary that preserves earlier interactions.
        var tailStart = stopDown;
        while (tailStart > 0 &&
               events[tailStart - 1].Type == ExactWheelInputEventType.MouseMove)
        {
            tailStart--;
        }

        events.RemoveRange(tailStart, finalStopUp - tailStart + 1);
    }

    private static void RemoveTerminalKeyboardStopInteraction(
        List<ExactWheelInputEvent> events)
    {
        if (events.Count == 0)
            return;

        const int virtualKeyEnter = 0x0D;
        const int virtualKeySpace = 0x20;
        var finalIndex = events.Count - 1;
        var finalEvent = events[finalIndex];
        if (!finalEvent.IsKeyboardEvent ||
            finalEvent.Data1 is not (virtualKeyEnter or virtualKeySpace))
        {
            return;
        }

        var virtualKey = finalEvent.Data1;
        var start = finalIndex;
        var hasKeyDown = finalEvent.Type == ExactWheelInputEventType.KeyDown;
        while (start > 0)
        {
            var previous = events[start - 1];
            if (!previous.IsKeyboardEvent || previous.Data1 != virtualKey)
                break;

            start--;
            hasKeyDown |= previous.Type == ExactWheelInputEventType.KeyDown;
        }

        if (hasKeyDown)
            events.RemoveRange(start, events.Count - start);
    }

    private static void RemoveTerminalGlobalHotkeyInteraction(
        List<ExactWheelInputEvent> events,
        MacroRecordingHotkey hotkey)
    {
        if (events.Count == 0)
            return;

        var finalIndex = events.Count - 1;
        var lowerBound = Math.Max(
            0,
            events.Count - MaximumGlobalHotkeyTailEvents);
        var finalTimestamp = events[finalIndex].TimestampMicroseconds;
        var modifierKeys = hotkey.ModifierVirtualKeys;
        var primaryDown = -1;
        for (var index = finalIndex; index >= lowerBound; index--)
        {
            var candidate = events[index];
            if (finalTimestamp < candidate.TimestampMicroseconds ||
                finalTimestamp - candidate.TimestampMicroseconds >
                    MaximumGlobalHotkeyTailMicroseconds)
            {
                break;
            }
            if (candidate.Type == ExactWheelInputEventType.MouseMove)
                continue;
            if (!candidate.IsKeyboardEvent ||
                (candidate.Data1 != hotkey.VirtualKey &&
                 !modifierKeys.Contains(candidate.Data1)))
            {
                return;
            }
            if (candidate.Data1 == hotkey.VirtualKey &&
                candidate.Type == ExactWheelInputEventType.KeyDown)
            {
                primaryDown = index;
                break;
            }
        }
        if (primaryDown < 0)
            return;

        var controlFound = !hotkey.Modifiers.HasFlag(
            MacroRecordingHotkeyModifiers.Control);
        var altFound = !hotkey.Modifiers.HasFlag(
            MacroRecordingHotkeyModifiers.Alt);
        var shiftFound = !hotkey.Modifiers.HasFlag(
            MacroRecordingHotkeyModifiers.Shift);
        var start = primaryDown;
        for (var index = primaryDown - 1; index >= lowerBound; index--)
        {
            var candidate = events[index];
            if (finalTimestamp < candidate.TimestampMicroseconds ||
                finalTimestamp - candidate.TimestampMicroseconds >
                    MaximumGlobalHotkeyTailMicroseconds)
            {
                break;
            }
            if (candidate.Type == ExactWheelInputEventType.MouseMove)
                continue;
            if (!candidate.IsKeyboardEvent)
                break;
            if (candidate.Data1 == hotkey.VirtualKey)
            {
                if (candidate.Type != ExactWheelInputEventType.KeyDown)
                    break;
                start = index;
                continue;
            }
            if (!modifierKeys.Contains(candidate.Data1) ||
                candidate.Type != ExactWheelInputEventType.KeyDown)
            {
                break;
            }

            start = index;
            controlFound |= IsControlVirtualKey(candidate.Data1);
            altFound |= IsAltVirtualKey(candidate.Data1);
            shiftFound |= IsShiftVirtualKey(candidate.Data1);
            if (controlFound && altFound && shiftFound)
                break;
        }

        if (!controlFound || !altFound || !shiftFound)
            return;

        // WM_HOTKEY is posted, not synchronously delivered with the low-level
        // hook callback. A busy UI thread can therefore leave key-up and mouse
        // move events after the primary key-down. Remove only the bounded,
        // recognized suffix and never cross an unrelated key/button boundary.
        events.RemoveRange(start, events.Count - start);
    }

    private static bool IsControlVirtualKey(int virtualKey) =>
        virtualKey is 0x11 or 0xA2 or 0xA3;

    private static bool IsAltVirtualKey(int virtualKey) =>
        virtualKey is 0x12 or 0xA4 or 0xA5;

    private static bool IsShiftVirtualKey(int virtualKey) =>
        virtualKey is 0x10 or 0xA0 or 0xA1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    private string Localize(string key, params object?[] arguments) =>
        arguments.Length == 0
            ? _localization.GetString(key)
            : _localization.Format(key, arguments);

    private sealed record StoppedRecordingCapture(
        ExactWheelRecordingTarget Target,
        SessionMacroKind Kind,
        Rect StopButtonBounds,
        StopActivationKind StopActivation,
        MacroRecordingHotkey StopHotkey,
        ExactWheelRecording? Captured,
        Exception? Failure);

    private enum RecorderState
    {
        Idle,
        Countdown,
        Recording,
        Stopping
    }
}

internal enum StopActivationKind
{
    Unknown,
    Mouse,
    Keyboard,
    GlobalHotkey
}

internal sealed class ClientMacroOutsideTargetException : InvalidOperationException
{
}
