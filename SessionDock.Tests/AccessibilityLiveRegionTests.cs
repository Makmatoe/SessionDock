using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AccessibilityLiveRegionTests
{
    [Theory]
    [InlineData(null, "Ready", true)]
    [InlineData(null, "   ", false)]
    [InlineData("Ready", "Ready", false)]
    [InlineData(" Ready ", "Ready", false)]
    [InlineData("Ready", "ready", true)]
    [InlineData("Ready", "Complete", true)]
    [InlineData("Ready", null, false)]
    public void ShouldAnnounce_RejectsBlankAndDuplicateAnnouncements(
        string? previous,
        string? next,
        bool expected)
    {
        Assert.Equal(
            expected,
            AccessibilityLiveRegion.ShouldAnnounce(previous, next));
    }

    [Fact]
    public void Update_SetsTextAndAutomationMetadataBeforeTheTargetLoads()
    {
        RunOnSta(() =>
        {
            var target = new TextBlock();
            var raisedCount = 0;
            var liveRegion = new AccessibilityLiveRegion(
                target,
                _ =>
                {
                    raisedCount++;
                    return true;
                });

            Assert.True(liveRegion.Update(
                "Saving...",
                "Saving account changes.",
                AccessibilityLiveRegionSeverity.Assertive));
            Assert.Equal("Saving...", target.Text);
            Assert.Equal(
                "Saving account changes.",
                AutomationProperties.GetName(target));
            Assert.Equal(
                AutomationLiveSetting.Assertive,
                AutomationProperties.GetLiveSetting(target));
            Assert.Equal(0, raisedCount);

            Assert.False(liveRegion.Update(
                "Saving...",
                "  Saving account changes.  ",
                AccessibilityLiveRegionSeverity.Polite));
            Assert.Equal(
                AutomationLiveSetting.Polite,
                AutomationProperties.GetLiveSetting(target));
            Assert.Equal(0, raisedCount);
        });
    }

    [Fact]
    public void Update_WaitsForLoadedAndVisibleAndRaisesEachChangeOnce()
    {
        RunOnSta(() =>
        {
            Window? window = null;
            try
            {
                var target = new TextBlock();
                var announcements = new List<string>();
                var liveRegion = new AccessibilityLiveRegion(
                    target,
                    element =>
                    {
                        Assert.True(element.IsLoaded);
                        Assert.True(element.IsVisible);
                        announcements.Add(
                            AutomationProperties.GetName(element));
                        return true;
                    });

                Assert.True(liveRegion.Update("Starting"));
                Assert.Empty(announcements);

                window = CreateOffscreenWindow(target);
                window.Show();
                FlushDispatcher();

                Assert.True(target.IsLoaded);
                Assert.True(target.IsVisible);
                Assert.Equal(["Starting"], announcements);

                Assert.False(liveRegion.Update("Starting"));
                Assert.Equal(["Starting"], announcements);

                window.Hide();
                FlushDispatcher();
                Assert.False(target.IsVisible);

                Assert.True(liveRegion.Update(
                    "Failed",
                    "Launch failed. Try again.",
                    AccessibilityLiveRegionSeverity.Assertive));
                Assert.Equal(["Starting"], announcements);

                window.Show();
                FlushDispatcher();
                Assert.Equal(
                    ["Starting", "Launch failed. Try again."],
                    announcements);

                Assert.False(liveRegion.Update(string.Empty));
                Assert.Equal(string.Empty, target.Text);
                Assert.Equal(
                    string.Empty,
                    AutomationProperties.GetName(target));

                Assert.True(liveRegion.Update("Starting"));
                Assert.Equal(
                    [
                        "Starting",
                        "Launch failed. Try again.",
                        "Starting"
                    ],
                    announcements);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void Update_DefaultAutomationPeerPathIsSafeOnALoadedTarget()
    {
        RunOnSta(() =>
        {
            Window? window = null;
            try
            {
                var target = new TextBlock();
                window = CreateOffscreenWindow(target);
                window.Show();
                FlushDispatcher();

                var liveRegion = new AccessibilityLiveRegion(target);
                var exception = Record.Exception(() => liveRegion.Update(
                    "Ready",
                    "SessionDock is ready.",
                    AccessibilityLiveRegionSeverity.Polite));

                Assert.Null(exception);
                Assert.NotNull(
                    UIElementAutomationPeer.FromElement(target));
                Assert.Equal(
                    "SessionDock is ready.",
                    AutomationProperties.GetName(target));
                Assert.Equal(
                    AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(target));
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void Update_RetriesOneTransientAutomationEventFailure()
    {
        RunOnSta(() =>
        {
            Window? window = null;
            try
            {
                var target = new TextBlock();
                var attempts = 0;
                var liveRegion = new AccessibilityLiveRegion(
                    target,
                    _ => ++attempts >= 2);
                window = CreateOffscreenWindow(target);
                window.Show();
                FlushDispatcher();

                Assert.True(liveRegion.Update("Ready"));
                Assert.Equal(1, attempts);

                FlushDispatcher();
                Assert.Equal(2, attempts);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void Update_BoundsRepeatedAutomationEventFailures()
    {
        RunOnSta(() =>
        {
            Window? window = null;
            try
            {
                var target = new TextBlock();
                var attempts = 0;
                var liveRegion = new AccessibilityLiveRegion(
                    target,
                    _ =>
                    {
                        attempts++;
                        return false;
                    });
                window = CreateOffscreenWindow(target);
                window.Show();
                FlushDispatcher();

                Assert.True(liveRegion.Update("Ready"));
                FlushDispatcher();
                FlushDispatcher();

                Assert.Equal(2, attempts);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    private static Window CreateOffscreenWindow(UIElement content)
    {
        return new Window
        {
            Content = content,
            Width = 100,
            Height = 100,
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
    }

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
