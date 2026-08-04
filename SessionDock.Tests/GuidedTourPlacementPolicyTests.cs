using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SessionDock.Tests;

public sealed class GuidedTourPlacementPolicyTests
{
    private const double EdgeMargin = 16;
    private const double PreferredGap = 14;
    private const double MinimumGap = 4;
    private const double MinimumWidth = 220;
    private const double MinimumHeight = 168;

    [Fact]
    public void Calculate_ScreenshotSizedThreshold_KeepsTargetVisibleBelow()
    {
        var highlight = new Rect(324, 140, 322, 118);

        var placement = Calculate(
            new Size(646, 515),
            highlight,
            new Size(360, 236));

        Assert.Equal(GuidedTourCalloutSide.Below, placement.Side);
        Assert.False(placement.Bounds.IntersectsWith(highlight));
        Assert.True(placement.Bounds.Top >= highlight.Bottom + MinimumGap);
        Assert.Equal(236, placement.Bounds.Height);
        AssertInsideViewport(placement.Bounds, new Size(646, 515));
    }

    [Fact]
    public void Calculate_UsesAboveWhenTargetIsNearBottom()
    {
        var highlight = new Rect(300, 480, 200, 80);

        var placement = Calculate(
            new Size(800, 600),
            highlight,
            new Size(360, 220));

        Assert.Equal(GuidedTourCalloutSide.Above, placement.Side);
        Assert.False(placement.Bounds.IntersectsWith(highlight));
        AssertInsideViewport(placement.Bounds, new Size(800, 600));
    }

    [Fact]
    public void Calculate_UsesSideRegionForMinimumWindow()
    {
        var highlight = new Rect(250, 125, 254, 132);

        var placement = Calculate(
            new Size(520, 430),
            highlight,
            new Size(360, 300));

        Assert.Equal(GuidedTourCalloutSide.Left, placement.Side);
        Assert.False(placement.Bounds.IntersectsWith(highlight));
        Assert.True(placement.Bounds.Width >= MinimumWidth);
        AssertInsideViewport(placement.Bounds, new Size(520, 430));
    }

    [Theory]
    [InlineData(18, 180, 180, 120, "Right")]
    [InlineData(602, 180, 180, 120, "Left")]
    public void Calculate_UsesHorizontalRegionAndClampsCrossAxis(
        double targetLeft,
        double targetTop,
        double targetWidth,
        double targetHeight,
        string expectedSide)
    {
        var viewport = new Size(800, 480);
        var highlight = new Rect(
            targetLeft,
            targetTop,
            targetWidth,
            targetHeight);

        var placement = Calculate(
            viewport,
            highlight,
            new Size(360, 300));

        Assert.Equal(expectedSide, placement.Side.ToString());
        Assert.False(placement.Bounds.IntersectsWith(highlight));
        AssertInsideViewport(placement.Bounds, viewport);
    }

    [Fact]
    public void Calculate_UnavoidableCollision_IsBoundedAndDeterministic()
    {
        var viewport = new Size(300, 240);
        var highlight = new Rect(0, 0, 300, 240);

        var first = Calculate(
            viewport,
            highlight,
            new Size(360, 300));
        var second = Calculate(
            viewport,
            highlight,
            new Size(360, 300));

        Assert.Equal(GuidedTourCalloutSide.Fallback, first.Side);
        Assert.Equal(first, second);
        AssertInsideViewport(first.Bounds, viewport);
    }

    [Fact]
    public void Overlay_ReportedAndMinimumSizes_KeepTargetAndActionsVisible()
    {
        RunOnSta(() =>
        {
            Window? window = null;
            try
            {
                var target = new Border
                {
                    Width = 322,
                    Height = 118
                };
                Canvas.SetLeft(target, 324);
                Canvas.SetTop(target, 140);
                var targetLayer = new Canvas();
                targetLayer.Children.Add(target);
                var overlay = new GuidedTourOverlay();
                var root = new Grid();
                root.Children.Add(targetLayer);
                root.Children.Add(overlay);
                window = new Window
                {
                    Content = root,
                    Width = 646,
                    Height = 515,
                    Left = -10_000,
                    Top = -10_000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };
                window.Show();
                FlushDispatcher();

                overlay.Start(
                    [
                        new GuidedTourStep(
                            target,
                            "Run a complete template",
                            "A template launches its accounts and restores scalable window positions. Assigned macros wait in the floating controller until you press Play.")
                    ],
                    "Step {0} of {1}",
                    "Back",
                    "Next",
                    "Finish",
                    "Skip tutorial");
                FlushDispatcher();
                FlushDispatcher();
                AssertOverlayGeometry(overlay);

                target.Width = 254;
                target.Height = 132;
                Canvas.SetLeft(target, 250);
                Canvas.SetTop(target, 125);
                window.Width = 520;
                window.Height = 430;
                FlushDispatcher();
                FlushDispatcher();
                AssertOverlayGeometry(overlay);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    private static GuidedTourCalloutPlacement Calculate(
        Size viewport,
        Rect highlight,
        Size desired) =>
        GuidedTourPlacementPolicy.Calculate(
            viewport,
            highlight,
            desired,
            EdgeMargin,
            PreferredGap,
            MinimumGap,
            MinimumWidth,
            MinimumHeight);

    private static void AssertInsideViewport(Rect bounds, Size viewport)
    {
        Assert.True(bounds.Left >= EdgeMargin - 0.01);
        Assert.True(bounds.Top >= EdgeMargin - 0.01);
        Assert.True(bounds.Right <= viewport.Width - EdgeMargin + 0.01);
        Assert.True(bounds.Bottom <= viewport.Height - EdgeMargin + 0.01);
    }

    private static void AssertOverlayGeometry(GuidedTourOverlay overlay)
    {
        var callout = BoundsRelativeTo(overlay.Callout, overlay);
        var target = BoundsRelativeTo(overlay.TargetOutline, overlay);
        Assert.False(callout.IntersectsWith(target));
        AssertInsideViewport(
            callout,
            new Size(overlay.ActualWidth, overlay.ActualHeight));
        foreach (var button in new[]
                 {
                     overlay.SkipButton,
                     overlay.BackButton,
                     overlay.NextButton
                 })
        {
            var buttonBounds = BoundsRelativeTo(button, overlay);
            Assert.True(
                callout.Contains(buttonBounds),
                $"{button.Name} must remain inside the tutorial card. " +
                $"Callout: {callout}; button: {buttonBounds}; " +
                $"viewport: {overlay.ActualWidth}x{overlay.ActualHeight}.");
        }
    }

    private static Rect BoundsRelativeTo(
        FrameworkElement element,
        Visual relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void FlushDispatcher()
    {
        Dispatcher.CurrentDispatcher.Invoke(
            DispatcherPriority.ApplicationIdle,
            static () => { });
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception currentException)
            {
                exception = currentException;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(15)),
            "The WPF STA test did not finish within 15 seconds.");
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
