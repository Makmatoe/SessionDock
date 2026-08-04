using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SessionDock.Services;

internal enum AccessibilityLiveRegionSeverity
{
    Polite,
    Assertive
}

internal sealed class AccessibilityLiveRegion
{
    private const int MaximumRaiseAttempts = 2;
    private readonly TextBlock _target;
    private readonly Func<TextBlock, bool> _raiseAutomationEvent;
    private bool _hasPreviousAnnouncement;
    private string _previousAnnouncement = string.Empty;
    private bool _announcementPending;
    private bool _waitingForAvailability;
    private bool _retryQueued;
    private int _raiseAttempts;

    internal AccessibilityLiveRegion(TextBlock target)
        : this(target, RaiseLiveRegionChanged)
    {
    }

    internal AccessibilityLiveRegion(
        TextBlock target,
        Func<TextBlock, bool> raiseAutomationEvent)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(raiseAutomationEvent);

        _target = target;
        _raiseAutomationEvent = raiseAutomationEvent;
    }

    internal bool Update(
        string? visibleText,
        string? accessibleAnnouncement = null,
        AccessibilityLiveRegionSeverity severity =
            AccessibilityLiveRegionSeverity.Polite,
        bool announceChanges = true)
    {
        _target.Dispatcher.VerifyAccess();

        var liveSetting = severity switch
        {
            AccessibilityLiveRegionSeverity.Polite =>
                AutomationLiveSetting.Polite,
            AccessibilityLiveRegionSeverity.Assertive =>
                AutomationLiveSetting.Assertive,
            _ => throw new ArgumentOutOfRangeException(
                nameof(severity),
                severity,
                "Unsupported live-region severity.")
        };

        var displayText = visibleText ?? string.Empty;
        var announcement = NormalizeAnnouncement(
            accessibleAnnouncement ?? displayText);
        var changed = !_hasPreviousAnnouncement ||
            !string.Equals(
                _previousAnnouncement,
                announcement,
                StringComparison.Ordinal);
        var shouldAnnounce = ShouldAnnounce(
            _hasPreviousAnnouncement ? _previousAnnouncement : null,
            announcement);

        // Status producers can report the same state repeatedly while polling.
        // Avoid invalidating WPF layout and UI Automation properties unless the
        // rendered value actually changed.
        if (!string.Equals(_target.Text, displayText, StringComparison.Ordinal))
            _target.Text = displayText;
        if (!string.Equals(
                AutomationProperties.GetName(_target),
                announcement,
                StringComparison.Ordinal))
        {
            AutomationProperties.SetName(_target, announcement);
        }
        if (AutomationProperties.GetLiveSetting(_target) != liveSetting)
            AutomationProperties.SetLiveSetting(_target, liveSetting);

        if (!announceChanges)
        {
            _hasPreviousAnnouncement = true;
            _previousAnnouncement = announcement;
            _announcementPending = false;
            _raiseAttempts = 0;
            StopWaitingForAvailability();
            return false;
        }

        if (!changed)
            return false;

        _hasPreviousAnnouncement = true;
        _previousAnnouncement = announcement;

        if (!shouldAnnounce)
        {
            _announcementPending = false;
            _raiseAttempts = 0;
            StopWaitingForAvailability();
            return false;
        }

        _announcementPending = true;
        _raiseAttempts = 0;
        TryRaisePendingAnnouncement();
        return true;
    }

    internal static bool ShouldAnnounce(
        string? previousAnnouncement,
        string? nextAnnouncement)
    {
        var next = NormalizeAnnouncement(nextAnnouncement);
        if (next.Length == 0)
            return false;

        return !string.Equals(
            NormalizeAnnouncement(previousAnnouncement),
            next,
            StringComparison.Ordinal);
    }

    private static string NormalizeAnnouncement(string? announcement)
    {
        return announcement?.Trim() ?? string.Empty;
    }

    private void TryRaisePendingAnnouncement()
    {
        if (!_announcementPending)
        {
            StopWaitingForAvailability();
            return;
        }

        if (!CanRaiseAutomationEvent())
        {
            StartWaitingForAvailability();
            return;
        }

        StopWaitingForAvailability();
        _raiseAttempts++;
        if (_raiseAutomationEvent(_target))
        {
            _announcementPending = false;
            _raiseAttempts = 0;
            return;
        }

        if (_raiseAttempts >= MaximumRaiseAttempts)
        {
            _announcementPending = false;
            return;
        }

        QueueAutomationRetry();
    }

    private void QueueAutomationRetry()
    {
        if (_retryQueued ||
            _target.Dispatcher.HasShutdownStarted ||
            _target.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _retryQueued = true;
        _ = _target.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () =>
            {
                _retryQueued = false;
                TryRaisePendingAnnouncement();
            });
    }

    private bool CanRaiseAutomationEvent()
    {
        return _target.IsLoaded &&
            _target.IsVisible &&
            !_target.Dispatcher.HasShutdownStarted &&
            !_target.Dispatcher.HasShutdownFinished;
    }

    private void StartWaitingForAvailability()
    {
        if (_waitingForAvailability ||
            _target.Dispatcher.HasShutdownStarted ||
            _target.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _target.Loaded += Target_Loaded;
        _target.IsVisibleChanged += Target_IsVisibleChanged;
        _waitingForAvailability = true;
    }

    private void StopWaitingForAvailability()
    {
        if (!_waitingForAvailability)
            return;

        _target.Loaded -= Target_Loaded;
        _target.IsVisibleChanged -= Target_IsVisibleChanged;
        _waitingForAvailability = false;
    }

    private void Target_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        TryRaisePendingAnnouncement();
    }

    private void Target_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        TryRaisePendingAnnouncement();
    }

    private static bool RaiseLiveRegionChanged(TextBlock target)
    {
        try
        {
            var peer = UIElementAutomationPeer.FromElement(target) ??
                UIElementAutomationPeer.CreatePeerForElement(target);
            if (peer is null)
                return false;

            peer.RaiseAutomationEvent(
                AutomationEvents.LiveRegionChanged);
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
