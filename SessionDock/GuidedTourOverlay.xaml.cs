using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SessionDock.Services;

namespace SessionDock;

public sealed record GuidedTourStep(
    FrameworkElement Target,
    string Title,
    string Body,
    Action? Prepare = null);

public partial class GuidedTourOverlay : UserControl
{
    private const double HighlightPadding = 7;
    private const double EdgeMargin = 16;
    private const double CalloutGap = 14;
    private const double MinimumCalloutGap = 4;
    private const double PreferredCalloutWidth = 360;
    private const double MinimumReadableCalloutWidth = 220;
    private const double MinimumReadableCalloutHeight = 168;
    private const double CompactCalloutWidth = 300;
    private IReadOnlyList<GuidedTourStep> _steps = [];
    private int _stepIndex;
    private bool _isFinishing;
    private string _nextText = string.Empty;
    private string _finishText = string.Empty;
    private readonly AccessibilityLiveRegion _announcementLiveRegion;
    private Rect _lastPlacementHighlight = Rect.Empty;
    private Size _lastPlacementViewport = Size.Empty;
    private int _lastPlacementStep = -1;
    private bool _isTrackingLayout;
    private bool _isUpdatingSpotlight;
    private DispatcherOperation? _spotlightUpdateOperation;
    private int _tourGeneration;

    public GuidedTourOverlay()
    {
        InitializeComponent();
        _announcementLiveRegion = new AccessibilityLiveRegion(TitleText);
        IsVisibleChanged += Overlay_IsVisibleChanged;
    }

    public event EventHandler? Completed;

    public event EventHandler? Skipped;

    public bool IsRunning => Visibility == Visibility.Visible &&
                             _steps.Count > 0;

    public void Start(
        IReadOnlyList<GuidedTourStep> steps,
        string progressFormat,
        string backText,
        string nextText,
        string finishText,
        string skipText)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0 || steps.Any(step => step.Target is null))
            throw new ArgumentException(
                "A guided tour requires at least one valid target.",
                nameof(steps));

        CancelPendingSpotlightUpdate();
        _tourGeneration++;
        _steps = steps.ToArray();
        _stepIndex = 0;
        _isFinishing = false;
        ProgressText.Tag = progressFormat;
        BackButton.Content = backText;
        _nextText = nextText;
        _finishText = finishText;
        SkipButton.Content = skipText;
        StartLayoutTracking();
        Visibility = Visibility.Visible;
        Panel.SetZIndex(this, int.MaxValue);
        ShowCurrentStep();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => NextButton.Focus());
    }

    public void Stop()
    {
        _tourGeneration++;
        CancelPendingSpotlightUpdate();
        _steps = [];
        _stepIndex = 0;
        StopLayoutTracking();
        InvalidateCalloutPlacement();
        Visibility = Visibility.Collapsed;
    }

    private void ShowCurrentStep()
    {
        if (!IsRunning || _stepIndex < 0 || _stepIndex >= _steps.Count)
            return;

        var step = _steps[_stepIndex];
        step.Prepare?.Invoke();
        // A tour step may target a control inside a scrolled settings or setup
        // workspace. Ask its nearest scrolling ancestor to reveal it before the
        // spotlight is measured; LayoutUpdated performs the final measurement
        // after WPF has completed any resulting scroll/layout work.
        step.Target.BringIntoView();
        var progressFormat = ProgressText.Tag as string ?? "{0} of {1}";
        var progress = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            progressFormat,
            _stepIndex + 1,
            _steps.Count);
        ProgressText.Text = progress;
        BodyText.Text = step.Body;
        _announcementLiveRegion.Update(
            step.Title,
            CreateAnnouncement(
                progress,
                step.Title,
                step.Body,
                PreviewText.Text),
            AccessibilityLiveRegionSeverity.Assertive);
        BackButton.IsEnabled = _stepIndex > 0;
        NextButton.Content = _stepIndex == _steps.Count - 1
            ? _finishText
            : _nextText;
        AutomationProperties.SetName(
            Callout,
            CreateAnnouncement(
                progress,
                TitleText.Text,
                BodyText.Text,
                PreviewText.Text));
        InvalidateCalloutPlacement();
        QueueSpotlightUpdate();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => NextButton.Focus());
    }

    private void UpdateSpotlight()
    {
        if (_isUpdatingSpotlight ||
            !IsRunning ||
            !IsFinitePositive(ActualWidth) ||
            !IsFinitePositive(ActualHeight))
            return;

        _isUpdatingSpotlight = true;
        try
        {
            UpdateSpotlightCore();
        }
        finally
        {
            _isUpdatingSpotlight = false;
        }
    }

    private void UpdateSpotlightCore()
    {
        var viewport = new Size(ActualWidth, ActualHeight);
        var viewportBounds = new Rect(0, 0, viewport.Width, viewport.Height);
        var targetBounds = GetVisibleTargetBounds(
            _steps[_stepIndex].Target,
            viewportBounds);

        if (!IsUsableRect(targetBounds))
            targetBounds = CreateFallbackTargetBounds(viewport);

        var highlight = InflateAndClamp(targetBounds, HighlightPadding);
        if (!IsUsableRect(highlight))
            highlight = CreateFallbackTargetBounds(viewport);
        if (_lastPlacementStep == _stepIndex &&
            AreClose(_lastPlacementViewport, viewport) &&
            AreClose(_lastPlacementHighlight, highlight))
        {
            return;
        }

        PositionElement(
            TargetOutline,
            highlight.Left,
            highlight.Top,
            highlight.Width,
            highlight.Height);
        PositionShade(
            TopShade,
            0,
            0,
            ActualWidth,
            Math.Max(0, highlight.Top));
        PositionShade(
            LeftShade,
            0,
            highlight.Top,
            Math.Max(0, highlight.Left),
            highlight.Height);
        PositionShade(
            RightShade,
            highlight.Right,
            highlight.Top,
            Math.Max(0, ActualWidth - highlight.Right),
            highlight.Height);
        PositionShade(
            BottomShade,
            0,
            highlight.Bottom,
            ActualWidth,
            Math.Max(0, ActualHeight - highlight.Bottom));

        var maximumCalloutWidth = Math.Max(
            1,
            ActualWidth - (EdgeMargin * 2));
        var maximumCalloutHeight = Math.Max(
            1,
            ActualHeight - (EdgeMargin * 2));
        SetCompactNavigation(compact: false);
        Callout.Width = Math.Min(
            PreferredCalloutWidth,
            maximumCalloutWidth);
        Callout.Height = double.NaN;
        Callout.MaxHeight = maximumCalloutHeight;
        Callout.Measure(new Size(
            maximumCalloutWidth,
            maximumCalloutHeight));
        var desiredCallout = new Size(
            Callout.Width,
            Math.Max(
                1,
                Math.Min(
                    Callout.DesiredSize.Height,
                    maximumCalloutHeight)));
        var placement = GuidedTourPlacementPolicy.Calculate(
            viewport,
            highlight,
            desiredCallout,
            EdgeMargin,
            CalloutGap,
            MinimumCalloutGap,
            MinimumReadableCalloutWidth,
            MinimumReadableCalloutHeight);
        SetCompactNavigation(
            placement.Bounds.Width < CompactCalloutWidth);
        Callout.Width = placement.Bounds.Width;
        Callout.Height = placement.Bounds.Height;
        Callout.MaxHeight = placement.Bounds.Height;
        Canvas.SetLeft(Callout, placement.Bounds.Left);
        Canvas.SetTop(Callout, placement.Bounds.Top);
        _lastPlacementStep = _stepIndex;
        _lastPlacementViewport = viewport;
        _lastPlacementHighlight = highlight;
    }

    private Rect GetVisibleTargetBounds(
        FrameworkElement target,
        Rect viewportBounds)
    {
        if (!target.IsVisible ||
            !IsFinitePositive(target.ActualWidth) ||
            !IsFinitePositive(target.ActualHeight))
        {
            return Rect.Empty;
        }

        try
        {
            var transform = target.TransformToVisual(this);
            var bounds = transform.TransformBounds(
                new Rect(0, 0, target.ActualWidth, target.ActualHeight));
            if (!IsUsableRect(bounds))
                return Rect.Empty;

            var visibleBounds = Rect.Intersect(bounds, viewportBounds);
            return IsUsableRect(visibleBounds)
                ? visibleBounds
                : Rect.Empty;
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
        catch (ArgumentException)
        {
            return Rect.Empty;
        }
    }

    private static Rect CreateFallbackTargetBounds(Size viewport)
    {
        var width = Math.Min(160, viewport.Width);
        var height = Math.Min(64, viewport.Height);
        return new Rect(
            Math.Max(0, (viewport.Width - width) / 2),
            Math.Max(0, (viewport.Height - height) / 2),
            width,
            height);
    }

    private static bool IsUsableRect(Rect bounds) =>
        !bounds.IsEmpty &&
        double.IsFinite(bounds.Left) &&
        double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Right) &&
        double.IsFinite(bounds.Bottom) &&
        IsFinitePositive(bounds.Width) &&
        IsFinitePositive(bounds.Height);

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;

    private Rect InflateAndClamp(Rect bounds, double padding)
    {
        var left = Math.Clamp(bounds.Left - padding, 0, ActualWidth);
        var top = Math.Clamp(bounds.Top - padding, 0, ActualHeight);
        var right = Math.Clamp(bounds.Right + padding, left, ActualWidth);
        var bottom = Math.Clamp(bounds.Bottom + padding, top, ActualHeight);
        return new Rect(left, top, right - left, bottom - top);
    }

    private void InvalidateCalloutPlacement()
    {
        _lastPlacementStep = -1;
        _lastPlacementViewport = Size.Empty;
        _lastPlacementHighlight = Rect.Empty;
    }

    private void QueueSpotlightUpdate()
    {
        if (!IsRunning ||
            _spotlightUpdateOperation is
            {
                Status: DispatcherOperationStatus.Pending or
                    DispatcherOperationStatus.Executing
            })
        {
            return;
        }

        var generation = _tourGeneration;
        _spotlightUpdateOperation = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                _spotlightUpdateOperation = null;
                if (generation != _tourGeneration || !IsRunning)
                    return;

                UpdateSpotlight();
            }));
    }

    private void CancelPendingSpotlightUpdate()
    {
        var pendingUpdate = _spotlightUpdateOperation;
        _spotlightUpdateOperation = null;
        if (pendingUpdate?.Status == DispatcherOperationStatus.Pending)
            pendingUpdate.Abort();
    }

    private void SetCompactNavigation(bool compact)
    {
        Callout.Padding = compact
            ? new Thickness(14)
            : new Thickness(18);
        CompactNavigationRow.Height = compact
            ? GridLength.Auto
            : new GridLength(0);
        Grid.SetColumnSpan(SkipButton, compact ? 3 : 1);
        Grid.SetRow(BackButton, compact ? 1 : 0);
        Grid.SetRow(NextButton, compact ? 1 : 0);
        BackButton.Margin = compact
            ? new Thickness(0, 8, 8, 0)
            : new Thickness(0, 0, 8, 0);
        NextButton.Margin = compact
            ? new Thickness(0, 8, 0, 0)
            : new Thickness(0);
    }

    private static bool AreClose(Size first, Size second) =>
        Math.Abs(first.Width - second.Width) < 0.5 &&
        Math.Abs(first.Height - second.Height) < 0.5;

    private static bool AreClose(Rect first, Rect second) =>
        !first.IsEmpty &&
        !second.IsEmpty &&
        Math.Abs(first.Left - second.Left) < 0.5 &&
        Math.Abs(first.Top - second.Top) < 0.5 &&
        Math.Abs(first.Width - second.Width) < 0.5 &&
        Math.Abs(first.Height - second.Height) < 0.5;

    private static void PositionShade(
        FrameworkElement element,
        double left,
        double top,
        double width,
        double height) =>
        PositionElement(element, left, top, width, height);

    private static void PositionElement(
        FrameworkElement element,
        double left,
        double top,
        double width,
        double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    internal static string CreateAnnouncement(
        string? progress,
        string? title,
        string? body,
        string? preview = null) =>
        string.Join(
            " ",
            new[] { progress, preview, title, body }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim()));

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_stepIndex < _steps.Count - 1)
        {
            _stepIndex++;
            ShowCurrentStep();
            return;
        }

        Finish(skipped: false);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_stepIndex <= 0)
            return;
        _stepIndex--;
        ShowCurrentStep();
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Finish(skipped: true);
    }

    private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key == Key.Escape)
        {
            Finish(skipped: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Left && _stepIndex > 0)
        {
            _stepIndex--;
            ShowCurrentStep();
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            NextButton_Click(NextButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Finish(bool skipped)
    {
        if (_isFinishing)
            return;
        _isFinishing = true;
        Stop();
        if (skipped)
            Skipped?.Invoke(this, EventArgs.Empty);
        else
            Completed?.Invoke(this, EventArgs.Empty);
    }

    private void Overlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        InvalidateCalloutPlacement();
        QueueSpotlightUpdate();
    }

    private void Overlay_LayoutUpdated(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (IsRunning)
            QueueSpotlightUpdate();
    }

    private void StartLayoutTracking()
    {
        if (_isTrackingLayout)
            return;
        LayoutUpdated += Overlay_LayoutUpdated;
        _isTrackingLayout = true;
    }

    private void StopLayoutTracking()
    {
        if (!_isTrackingLayout)
            return;
        LayoutUpdated -= Overlay_LayoutUpdated;
        _isTrackingLayout = false;
    }

    private void Overlay_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.NewValue is true)
        {
            StartLayoutTracking();
            QueueSpotlightUpdate();
        }
        else
        {
            StopLayoutTracking();
            CancelPendingSpotlightUpdate();
        }
    }
}
