using SessionDock.ExactWheel;

namespace SessionDock.Services;

internal enum ClientRecordingAdmissionFailureKind
{
    AuthorizationUnavailable,
    FocusLostWhileInputHeld,
    PointerLeftWhileButtonHeld,
    InputStillHeldAtStop
}

internal sealed record ClientRecordingAdmissionFailure(
    ClientRecordingAdmissionFailureKind Kind);

internal sealed class ClientRecordingAdmissionPolicy
{
    private readonly object _sync = new();
    private readonly ExactWheelRect _clientRect;
    private readonly Func<
        ExactWheelInputEvent,
        ExactWheelDispatchAuthorization> _authorize;
    private readonly HashSet<int> _heldKeyboardKeys = [];
    private readonly HashSet<int> _heldMouseButtons = [];
    private readonly HashSet<int> _suppressedKeyboardKeys = [];
    private readonly HashSet<int> _suppressedMouseButtons = [];
    private ClientRecordingAdmissionFailure? _failure;

    internal ClientRecordingAdmissionPolicy(
        ExactWheelRect clientRect,
        Func<ExactWheelInputEvent, ExactWheelDispatchAuthorization> authorize)
    {
        if (clientRect.Width <= 0 || clientRect.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(clientRect));
        _clientRect = clientRect;
        _authorize = authorize ??
            throw new ArgumentNullException(nameof(authorize));
    }

    internal ClientRecordingAdmissionFailure? Failure
    {
        get
        {
            lock (_sync)
                return _failure;
        }
    }

    internal bool TryAdmit(ExactWheelInputEvent inputEvent)
    {
        lock (_sync)
        {
            if (_failure is not null)
                return false;

            ExactWheelDispatchAuthorization authorization;
            try
            {
                authorization = _authorize(inputEvent);
            }
            catch (Exception)
            {
                FailLocked(
                    ClientRecordingAdmissionFailureKind
                        .AuthorizationUnavailable);
                return false;
            }

            if (authorization != ExactWheelDispatchAuthorization.Authorized)
            {
                if (_heldKeyboardKeys.Count > 0 ||
                    _heldMouseButtons.Count > 0)
                {
                    FailLocked(
                        ClientRecordingAdmissionFailureKind
                            .FocusLostWhileInputHeld);
                }
                TrackSuppressedTransition(inputEvent);
                return false;
            }

            if (ReleaseSuppressedTransition(inputEvent) ||
                IsPartOfSuppressedTransition(inputEvent))
            {
                return false;
            }

            if (inputEvent.IsMouseEvent &&
                !_clientRect.Contains(inputEvent.X, inputEvent.Y))
            {
                if (_heldMouseButtons.Count > 0)
                {
                    FailLocked(
                        ClientRecordingAdmissionFailureKind
                            .PointerLeftWhileButtonHeld);
                }
                TrackSuppressedTransition(inputEvent);
                return false;
            }

            return inputEvent.Type switch
            {
                ExactWheelInputEventType.KeyDown =>
                    AdmitDown(_heldKeyboardKeys, inputEvent.Data1),
                ExactWheelInputEventType.KeyUp =>
                    _heldKeyboardKeys.Remove(inputEvent.Data1),
                ExactWheelInputEventType.MouseButtonDown =>
                    AdmitDown(_heldMouseButtons, inputEvent.Data1),
                ExactWheelInputEventType.MouseButtonUp =>
                    _heldMouseButtons.Remove(inputEvent.Data1),
                _ => true
            };
        }
    }

    internal void Complete(
        IReadOnlySet<int> allowedTerminalKeyboardKeys,
        int requiredTerminalKey,
        int maximumTerminalKeyboardKeys)
    {
        ArgumentNullException.ThrowIfNull(allowedTerminalKeyboardKeys);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTerminalKeyboardKeys);
        lock (_sync)
        {
            if (_failure is not null)
                return;
            if (_heldMouseButtons.Count == 0 &&
                _heldKeyboardKeys.Count == 0)
            {
                return;
            }
            if (_heldMouseButtons.Count == 0 &&
                requiredTerminalKey > 0 &&
                _heldKeyboardKeys.Contains(requiredTerminalKey) &&
                _heldKeyboardKeys.Count <= maximumTerminalKeyboardKeys &&
                _heldKeyboardKeys.All(allowedTerminalKeyboardKeys.Contains))
            {
                return;
            }

            FailLocked(
                ClientRecordingAdmissionFailureKind.InputStillHeldAtStop);
        }
    }

    internal static bool HasBalancedTransitions(
        IReadOnlyList<ExactWheelInputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var heldKeyboardKeys = new HashSet<int>();
        var heldMouseButtons = new HashSet<int>();
        foreach (var inputEvent in events)
        {
            switch (inputEvent.Type)
            {
                case ExactWheelInputEventType.KeyDown:
                    _ = heldKeyboardKeys.Add(inputEvent.Data1);
                    break;
                case ExactWheelInputEventType.KeyUp:
                    if (!heldKeyboardKeys.Remove(inputEvent.Data1))
                        return false;
                    break;
                case ExactWheelInputEventType.MouseButtonDown:
                    _ = heldMouseButtons.Add(inputEvent.Data1);
                    break;
                case ExactWheelInputEventType.MouseButtonUp:
                    if (!heldMouseButtons.Remove(inputEvent.Data1))
                        return false;
                    break;
            }
        }

        return heldKeyboardKeys.Count == 0 && heldMouseButtons.Count == 0;
    }

    private static bool AdmitDown(HashSet<int> heldInputs, int input)
    {
        _ = heldInputs.Add(input);
        return true;
    }

    private void TrackSuppressedTransition(ExactWheelInputEvent inputEvent)
    {
        switch (inputEvent.Type)
        {
            case ExactWheelInputEventType.KeyDown:
                _ = _suppressedKeyboardKeys.Add(inputEvent.Data1);
                break;
            case ExactWheelInputEventType.KeyUp:
                _ = _suppressedKeyboardKeys.Remove(inputEvent.Data1);
                break;
            case ExactWheelInputEventType.MouseButtonDown:
                _ = _suppressedMouseButtons.Add(inputEvent.Data1);
                break;
            case ExactWheelInputEventType.MouseButtonUp:
                _ = _suppressedMouseButtons.Remove(inputEvent.Data1);
                break;
        }
    }

    private bool ReleaseSuppressedTransition(
        ExactWheelInputEvent inputEvent) =>
        inputEvent.Type switch
        {
            ExactWheelInputEventType.KeyUp =>
                _suppressedKeyboardKeys.Remove(inputEvent.Data1),
            ExactWheelInputEventType.MouseButtonUp =>
                _suppressedMouseButtons.Remove(inputEvent.Data1),
            _ => false
        };

    private bool IsPartOfSuppressedTransition(
        ExactWheelInputEvent inputEvent) =>
        inputEvent.Type switch
        {
            ExactWheelInputEventType.KeyDown =>
                _suppressedKeyboardKeys.Contains(inputEvent.Data1),
            ExactWheelInputEventType.MouseButtonDown =>
                _suppressedMouseButtons.Contains(inputEvent.Data1),
            _ when inputEvent.IsMouseEvent =>
                _suppressedMouseButtons.Count > 0,
            _ => false
        };

    private void FailLocked(ClientRecordingAdmissionFailureKind kind)
    {
        _failure ??= new ClientRecordingAdmissionFailure(kind);
    }
}
