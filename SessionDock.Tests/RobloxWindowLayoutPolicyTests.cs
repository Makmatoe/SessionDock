using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RobloxWindowLayoutPolicyTests
{
    [Fact]
    public void CreateCascade_BuildsTopLeftDiagonalWithHumanClientReveal()
    {
        var monitor = Monitor(
            @"\\.\DISPLAY1",
            index: 0,
            primary: true,
            new RobloxPixelRect(0, 0, 1920, 1080));
        var windows = Windows(3);

        var plan = RobloxWindowLayoutPolicy.CreateCascade(
            windows,
            [monitor],
            RobloxCascadeLayoutOptions.Default);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(1, plan.GroupCount);
        Assert.Equal(
            [
                new RobloxPixelRect(16, 16, 816, 640),
                new RobloxPixelRect(72, 96, 816, 640),
                new RobloxPixelRect(128, 176, 816, 640)
            ],
            plan.Placements.Select(item => item.OuterBounds));
        Assert.Equal([0, 1, 2], plan.Placements.Select(item => item.ZOrderFromBottom));
        Assert.Equal([0, 1, 2], plan.Placements.Select(item => item.CascadeIndex));
    }

    [Fact]
    public void CreateCascade_ScalesRevealMarginFrameAndTargetForMonitorDpi()
    {
        var monitor = Monitor(
            @"\\.\DISPLAY1",
            index: 0,
            primary: true,
            new RobloxPixelRect(-3840, 0, 3840, 2160),
            dpi: 144);

        var plan = RobloxWindowLayoutPolicy.CreateCascade(
            Windows(2),
            [monitor],
            RobloxCascadeLayoutOptions.Default);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(
            new RobloxPixelRect(-3816, 24, 1224, 960),
            plan.Placements[0].OuterBounds);
        Assert.Equal(
            new RobloxPixelRect(-3732, 144, 1224, 960),
            plan.Placements[1].OuterBounds);
    }

    [Fact]
    public void CreateCascade_ReducesTargetOnlyAsFarAsConfiguredOrObservedMinimum()
    {
        var monitor = Monitor(
            @"\\.\DISPLAY1",
            index: 0,
            primary: true,
            new RobloxPixelRect(0, 0, 1000, 700));
        var windows = Windows(3)
            .Select(window => window with
            {
                ObservedMinimumOuterSizeAt96Dpi =
                    new RobloxPixelSize(500, 500)
            })
            .ToArray();

        var plan = RobloxWindowLayoutPolicy.CreateCascade(
            windows,
            [monitor],
            RobloxCascadeLayoutOptions.Default);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(508, plan.Placements[0].OuterBounds.Height);
        Assert.All(
            plan.Placements,
            placement => Assert.True(placement.OuterBounds.Height >= 500));
    }

    [Fact]
    public void CreateCascade_UsesNextMonitorThenDeterministicAdditionalGroup()
    {
        var monitors = new[]
        {
            Monitor(
                @"\\.\DISPLAY1",
                0,
                true,
                new RobloxPixelRect(0, 0, 800, 600)),
            Monitor(
                @"\\.\DISPLAY2",
                1,
                false,
                new RobloxPixelRect(800, 0, 800, 600))
        };
        var options = RobloxCascadeLayoutOptions.Default with
        {
            TargetClientSizeAt96Dpi = new RobloxPixelSize(700, 500),
            MinimumClientSizeAt96Dpi = new RobloxPixelSize(700, 500)
        };

        var plan = RobloxWindowLayoutPolicy.CreateCascade(
            Windows(3),
            monitors,
            options);

        Assert.True(plan.Success, plan.Error);
        Assert.True(plan.RequiresGroupSwitch);
        Assert.Equal(2, plan.GroupCount);
        Assert.Equal([0, 1, 0], plan.Placements.Select(item => item.Monitor.Index));
        Assert.Equal([0, 0, 1], plan.Placements.Select(item => item.GroupIndex));
    }

    [Fact]
    public void CreateCascade_UsesConfiguredPreferredMonitorFirst()
    {
        var monitors = new[]
        {
            Monitor(
                @"\\.\DISPLAY1",
                0,
                true,
                new RobloxPixelRect(0, 0, 1920, 1080)),
            Monitor(
                @"\\.\DISPLAY2",
                1,
                false,
                new RobloxPixelRect(1920, 0, 1920, 1080))
        };
        var options = RobloxCascadeLayoutOptions.Default with
        {
            PreferredMonitorDeviceName = @"\\.\DISPLAY2"
        };

        var plan = RobloxWindowLayoutPolicy.CreateCascade(
            Windows(2),
            monitors,
            options);

        Assert.True(plan.Success, plan.Error);
        Assert.All(
            plan.Placements,
            item => Assert.Equal(@"\\.\DISPLAY2", item.Monitor.DeviceName));
    }

    [Fact]
    public void CreateCascade_FailsInsteadOfShrinkingBelowMinimum()
    {
        var plan = RobloxWindowLayoutPolicy.CreateCascade(
            Windows(1),
            [Monitor(
                @"\\.\DISPLAY1",
                0,
                true,
                new RobloxPixelRect(0, 0, 640, 480))],
            RobloxCascadeLayoutOptions.Default with
            {
                MinimumClientSizeAt96Dpi = new RobloxPixelSize(800, 600)
            });

        Assert.False(plan.Success);
        Assert.Empty(plan.Placements);
        Assert.Contains("minimum", plan.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateCascade_TargetBelowMinimumIsRaisedToMinimum()
    {
        var options = RobloxCascadeLayoutOptions.Default with
        {
            TargetClientSizeAt96Dpi = new RobloxPixelSize(320, 240),
            MinimumClientSizeAt96Dpi = new RobloxPixelSize(640, 480)
        };

        var plan = RobloxWindowLayoutPolicy.CreateCascade(
            Windows(1),
            [Monitor(
                @"\\.\DISPLAY1",
                0,
                true,
                new RobloxPixelRect(0, 0, 1920, 1080))],
            options);

        Assert.True(plan.Success, plan.Error);
        Assert.Equal(new RobloxPixelSize(656, 520), new RobloxPixelSize(
            plan.Placements[0].OuterBounds.Width,
            plan.Placements[0].OuterBounds.Height));
    }

    [Fact]
    public void NormalizedBounds_RestoreFourKCaptureAt1080P()
    {
        var source = Monitor(
            @"\\.\DISPLAY1",
            0,
            true,
            new RobloxPixelRect(0, 0, 3840, 2080));
        var saved = RobloxWindowLayoutPolicy.CaptureNormalizedBounds(
            new RobloxPixelRect(384, 208, 1920, 1040),
            [source]);

        Assert.NotNull(saved);
        var restored = RobloxWindowLayoutPolicy.RestoreNormalizedBounds(
            saved,
            [Monitor(
                @"\\.\DISPLAY9",
                0,
                true,
                new RobloxPixelRect(0, 0, 1920, 1040))],
            new RobloxPixelSize(320, 240));

        Assert.True(restored.Success, restored.Error);
        Assert.Equal(
            new RobloxPixelRect(192, 104, 960, 520),
            restored.Bounds);
    }

    [Fact]
    public void RestoreNormalizedBounds_SelectsDeviceThenPrimaryAndUsesOrdinalOnlyForLegacy()
    {
        var monitors = new[]
        {
            Monitor(
                @"\\.\DISPLAY1",
                0,
                true,
                new RobloxPixelRect(0, 0, 1920, 1080)),
            Monitor(
                @"\\.\DISPLAY2",
                1,
                false,
                new RobloxPixelRect(-2560, -200, 2560, 1440))
        };
        var disconnectedNamed = new RobloxNormalizedWindowBounds(
            @"\\.\MISSING",
            1,
            100_000,
            100_000,
            500_000,
            500_000);

        var primaryFallback = RobloxWindowLayoutPolicy.RestoreNormalizedBounds(
            disconnectedNamed,
            monitors,
            new RobloxPixelSize(320, 240));
        var legacyOrdinal = RobloxWindowLayoutPolicy.RestoreNormalizedBounds(
            disconnectedNamed with { PreferredMonitorDeviceName = null },
            monitors,
            new RobloxPixelSize(320, 240));
        var exactDevice = RobloxWindowLayoutPolicy.RestoreNormalizedBounds(
            disconnectedNamed with
            {
                PreferredMonitorDeviceName = @"\\.\DISPLAY2",
                PreferredMonitorIndex = 0
            },
            monitors,
            new RobloxPixelSize(320, 240));
        var fewerMonitors = RobloxWindowLayoutPolicy.RestoreNormalizedBounds(
            disconnectedNamed,
            [monitors[0]],
            new RobloxPixelSize(320, 240));

        Assert.True(primaryFallback.Success, primaryFallback.Error);
        Assert.True(primaryFallback.Monitor!.IsPrimary);
        Assert.Equal(
            new RobloxPixelRect(192, 108, 960, 540),
            primaryFallback.Bounds);
        Assert.True(legacyOrdinal.Success, legacyOrdinal.Error);
        Assert.Equal(1, legacyOrdinal.Monitor!.Index);
        Assert.Equal(
            new RobloxPixelRect(-2304, -56, 1280, 720),
            legacyOrdinal.Bounds);
        Assert.True(exactDevice.Success, exactDevice.Error);
        Assert.Equal(@"\\.\DISPLAY2", exactDevice.Monitor!.DeviceName);
        Assert.True(fewerMonitors.Success, fewerMonitors.Error);
        Assert.True(fewerMonitors.Monitor!.IsPrimary);
        Assert.Equal(new RobloxPixelRect(192, 108, 960, 540), fewerMonitors.Bounds);
    }

    [Fact]
    public void RestoreNormalizedBounds_StableIdentityOverridesReusedLogicalDisplayName()
    {
        var primary = Monitor(
            @"\\.\DISPLAY1",
            0,
            true,
            new RobloxPixelRect(0, 0, 1920, 1040),
            stableId: "monitor-primary");
        var reusedLogicalName = Monitor(
            @"\\.\DISPLAY2",
            1,
            false,
            new RobloxPixelRect(1920, 0, 1920, 1040),
            stableId: "monitor-replacement");
        var saved = new RobloxNormalizedWindowBounds(
            @"\\.\DISPLAY2",
            1,
            100_000,
            100_000,
            500_000,
            500_000)
        {
            PreferredMonitorStableId = "monitor-original"
        };

        var disconnected = RobloxWindowLayoutPolicy.RestoreNormalizedBounds(
            saved,
            [primary, reusedLogicalName],
            new RobloxPixelSize(320, 240));
        var exactStable = RobloxWindowLayoutPolicy.RestoreNormalizedBounds(
            saved with { PreferredMonitorStableId = "monitor-replacement" },
            [primary, reusedLogicalName],
            new RobloxPixelSize(320, 240));

        Assert.True(disconnected.Success, disconnected.Error);
        Assert.Same(primary, disconnected.Monitor);
        Assert.True(exactStable.Success, exactStable.Error);
        Assert.Same(reusedLogicalName, exactStable.Monitor);
    }

    [Fact]
    public void CaptureAndRestoreNormalizedBounds_UsesOffsetTaskbarWorkArea()
    {
        var monitor = Monitor(
            @"\\.\DISPLAY1",
            0,
            true,
            new RobloxPixelRect(0, 40, 1920, 1000),
            stableId: "monitor-primary",
            bounds: new RobloxPixelRect(0, 0, 1920, 1080));

        var saved = RobloxWindowLayoutPolicy.CaptureNormalizedBounds(
            new RobloxPixelRect(192, 140, 960, 500),
            [monitor]);
        var restored = RobloxWindowLayoutPolicy.RestoreNormalizedBounds(
            saved!,
            [monitor],
            new RobloxPixelSize(320, 240));

        Assert.NotNull(saved);
        Assert.Equal("monitor-primary", saved.PreferredMonitorStableId);
        Assert.Equal(100_000, saved.TopMillionths);
        Assert.True(restored.Success, restored.Error);
        Assert.Equal(new RobloxPixelRect(192, 140, 960, 500), restored.Bounds);
    }

    [Fact]
    public void RestoreNormalizedBounds_UsesDestinationDpiForMinimumAndClampsPosition()
    {
        var saved = new RobloxNormalizedWindowBounds(
            @"\\.\DISPLAY1",
            0,
            900_000,
            900_000,
            100_000,
            100_000);
        var destination = Monitor(
            @"\\.\DISPLAY1",
            0,
            true,
            new RobloxPixelRect(-1000, -500, 1000, 800),
            dpi: 144);

        var restored = RobloxWindowLayoutPolicy.RestoreNormalizedBounds(
            saved,
            [destination],
            new RobloxPixelSize(320, 240));

        Assert.True(restored.Success, restored.Error);
        Assert.Equal(
            new RobloxPixelRect(-480, -60, 480, 360),
            restored.Bounds);
    }

    [Fact]
    public void CaptureNormalizedBounds_UsesGreatestIntersectionOnNegativeMonitor()
    {
        var monitors = new[]
        {
            Monitor(
                @"\\.\DISPLAY1",
                0,
                true,
                new RobloxPixelRect(0, 0, 1920, 1080)),
            Monitor(
                @"\\.\DISPLAY2",
                1,
                false,
                new RobloxPixelRect(-1920, 0, 1920, 1080))
        };

        var saved = RobloxWindowLayoutPolicy.CaptureNormalizedBounds(
            new RobloxPixelRect(-1500, 100, 1000, 700),
            monitors);

        Assert.NotNull(saved);
        Assert.Equal(@"\\.\DISPLAY2", saved.PreferredMonitorDeviceName);
        Assert.Equal(1, saved.PreferredMonitorIndex);
    }

    [Fact]
    public void CaptureNormalizedBounds_FullyDisconnectedGeometryIsRejected()
    {
        var monitors = new[]
        {
            Monitor(
                @"\\.\DISPLAY1",
                0,
                true,
                new RobloxPixelRect(0, 0, 1920, 1040)),
            Monitor(
                @"\\.\DISPLAY2",
                1,
                false,
                new RobloxPixelRect(-1080, 200, 1080, 1920),
                dpi: 144)
        };

        var saved = RobloxWindowLayoutPolicy.CaptureNormalizedBounds(
            new RobloxPixelRect(-21_333, -21_333, 160, 30),
            monitors);

        Assert.Null(saved);
    }

    private static RobloxCascadeWindow[] Windows(int count) =>
        Enumerable.Range(1, count)
            .Select(index => new RobloxCascadeWindow(
                $"client-{index}",
                new RobloxWindowFrameInsets(8, 32, 8, 8),
                default))
            .ToArray();

    private static RobloxMonitor Monitor(
        string deviceName,
        int index,
        bool primary,
        RobloxPixelRect workArea,
        uint dpi = 96,
        string? stableId = null,
        RobloxPixelRect? bounds = null) =>
        new(
            deviceName,
            index,
            primary,
            bounds ?? workArea,
            workArea,
            dpi,
            dpi)
        {
            StableId = stableId
        };
}
