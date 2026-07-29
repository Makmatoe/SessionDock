using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace SessionDock.Services;

internal enum AccessibilityLiveRegionSeverity
{
    Polite,
    Assertive
}

internal sealed class AccessibilityLiveRegion
{
    private readonly TextBlock _target;
    private readonly Func<TextBlock, bool> _raiseAutomationEvent;
    private bool _hasPreviousAnnouncement;
    private string _previousAnnouncement = string.Empty;
    private bool _announcementPending;
    private bool _waitingForAvailability;

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
            AccessibilityLiveRegionSeverity.Polite)
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

        _target.Text = displayText;
        AutomationProperties.SetName(_target, announcement);
        AutomationProperties.SetLiveSetting(_target, liveSetting);

        if (!changed)
            return false;

        _hasPreviousAnnouncement = true;
        _previousAnnouncement = announcement;

        if (!shouldAnnounce)
        {
            _announcementPending = false;
            StopWaitingForAvailability();
            return false;
        }

        _announcementPending = true;
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

        _announcementPending = false;
        StopWaitingForAvailability();
        _ = _raiseAutomationEvent(_target);
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
