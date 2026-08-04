using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RobloxSessionLayoutCoordinatorTests
{
    private static readonly RobloxClientProcessIdentity FirstIdentity = new(
        42,
        new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc),
        @"C:\Roblox\version-a\RobloxPlayerBeta.exe");

    private static readonly RobloxClientProcessIdentity SecondIdentity = new(
        43,
        new DateTime(2026, 8, 3, 12, 0, 1, DateTimeKind.Utc),
        @"C:\Roblox\version-b\RobloxPlayerBeta.exe");

    [Fact]
    public async Task ArrangeAsync_UsesPreferencesRealizedBoundsAndExactZOrder()
    {
        var native = ReadyNative();
        native.Windows[(nint)101].MinimumOuterSize =
            new RobloxPixelSize(900, 700);
        var coordinator = CreateCoordinator(native);

        var result = await coordinator.ArrangeAsync(
            Windows(),
            Preferences(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError ?? result.ZOrderError);
        Assert.True(result.ZOrderRequested);
        Assert.True(result.ZOrderApplied);
        Assert.Equal(
            [
                new RobloxWindowZOrderPlacement((nint)101, nint.Zero),
                new RobloxWindowZOrderPlacement((nint)100, (nint)101)
            ],
            native.AppliedZOrderPlacements);
        Assert.Equal(
            new RobloxPixelRect(16, 16, 816, 640),
            native.RequestedBounds[(nint)100]);
        Assert.Equal(
            new RobloxPixelRect(80, 84, 816, 640),
            native.RequestedBounds[(nint)101]);
        Assert.Equal(
            new RobloxPixelRect(80, 84, 900, 700),
            result.Items[1].RealizedBounds);
        Assert.True(result.Items[1].WasClamped);
        Assert.Equal(900d / 1920d, result.Items[1].Placement!.Width, 6);
        Assert.Equal([0, 1], result.Items.Select(item => item.ZOrderFromBottom));
    }

    [Fact]
    public async Task ArrangeAsync_ContinuesAfterOneMoveFailsAndOrdersSuccesses()
    {
        var native = ReadyNative();
        native.Windows[(nint)101].AllowSetBounds = false;
        var coordinator = CreateCoordinator(native);

        var result = await coordinator.ArrangeAsync(
            Windows(),
            Preferences(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.HasPartialFailures);
        Assert.True(result.Items[0].Success);
        Assert.False(result.Items[1].Success);
        Assert.Equal(
            RobloxSessionLayoutStage.Move,
            result.Items[1].FailureStage);
        Assert.Equal(
            RobloxWindowOperationStatus.MoveFailed,
            result.Items[1].OperationStatus);
        Assert.True(result.ZOrderApplied);
        Assert.Empty(native.AppliedZOrderPlacements);
    }

    [Fact]
    public async Task ArrangeAsync_StartsOnPreferredMonitor()
    {
        var native = ReadyNative();
        native.Monitors =
        [
            Monitor(@"\\.\DISPLAY1", 0, true, 0, 0, 1920, 1080),
            Monitor(@"\\.\DISPLAY2", 1, false, 1920, 0, 1920, 1080)
        ];
        var preferences = Preferences();
        preferences.PreferredMonitorDeviceName = @"\\.\DISPLAY2";
        var coordinator = CreateCoordinator(native);

        var result = await coordinator.ArrangeAsync(
            Windows(),
            preferences,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError ?? result.ZOrderError);
        Assert.All(
            result.Items,
            item => Assert.Equal(@"\\.\DISPLAY2", item.Placement!.MonitorDeviceName));
        Assert.Equal(1936, result.Items[0].RequestedBounds.Left);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public async Task ArrangeAsync_ForceRefreshesSharedExecutableOnce(
        int targetCount)
    {
        var (native, windows) = ReadyNative(targetCount);

        var result = await CreateCoordinator(native).ArrangeAsync(
            windows,
            Preferences(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError ?? result.ZOrderError);
        AssertOneForcedTrustRefresh(native);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public async Task CapturePlacementsAsync_ForceRefreshesSharedExecutableOnce(
        int targetCount)
    {
        var (native, windows) = ReadyNative(targetCount);

        var result = await CreateCoordinator(native).CapturePlacementsAsync(
            windows,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError);
        AssertOneForcedTrustRefresh(native);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public async Task RestorePlacementsAsync_ForceRefreshesSharedExecutableOnce(
        int targetCount)
    {
        var (native, windows) = ReadyNative(targetCount);
        var placements = windows.ToDictionary(
            window => window.Key,
            _ => new NormalizedClientWindowPlacement
            {
                MonitorDeviceName = @"\\.\DISPLAY1",
                MonitorIndex = 0,
                Left = 0.1,
                Top = 0.1,
                Width = 0.5,
                Height = 0.5
            },
            StringComparer.OrdinalIgnoreCase);

        var result = await CreateCoordinator(native).RestorePlacementsAsync(
            windows,
            placements,
            Preferences(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError ?? result.ZOrderError);
        AssertOneForcedTrustRefresh(native);
    }

    [Fact]
    public async Task CapturePlacementsAsync_ProducesWorkAreaFractionsForFourK()
    {
        var native = ReadyNative();
        native.Monitors =
        [Monitor(
            @"\\.\DISPLAY1",
            0,
            true,
            0,
            0,
            3840,
            2080,
            stableId: "monitor-primary")];
        native.Windows[(nint)100].OuterBounds =
            new RobloxPixelRect(384, 208, 1920, 1040);
        native.Windows[(nint)100].ClientBounds =
            new RobloxPixelRect(392, 240, 1904, 1000);
        var coordinator = CreateCoordinator(native);

        var result = await coordinator.CapturePlacementsAsync(
            [Windows()[0]],
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError);
        var placement = Assert.Single(result.Items).Placement!;
        Assert.Equal(0.1, placement.Left, 6);
        Assert.Equal(0.1, placement.Top, 6);
        Assert.Equal(0.5, placement.Width, 6);
        Assert.Equal(0.5, placement.Height, 6);
        Assert.Equal("monitor-primary", placement.MonitorStableId);
    }

    [Fact]
    public async Task CapturePlacementsAsync_RejectsMinimizedIconicGeometry()
    {
        var native = ReadyNative();
        native.Windows[(nint)100].Minimized = true;
        native.Windows[(nint)100].OuterBounds =
            new RobloxPixelRect(-21_333, -21_333, 160, 30);
        native.Windows[(nint)100].ClientBounds =
            new RobloxPixelRect(-21_333, -21_333, 150, 20);
        var coordinator = CreateCoordinator(native);

        var result = await coordinator.CapturePlacementsAsync(
            [Windows()[0]],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        var item = Assert.Single(result.Items);
        Assert.Equal(
            RobloxSessionLayoutStage.InitialCapture,
            item.FailureStage);
        Assert.Equal(
            RobloxWindowOperationStatus.WindowUnavailable,
            item.OperationStatus);
        Assert.Null(item.Placement);
    }

    [Fact]
    public async Task RestorePlacementsAsync_ScalesFourKLayoutTo1080WorkArea()
    {
        var native = ReadyNative();
        native.Monitors =
        [Monitor(@"\\.\DISPLAY9", 0, true, 0, 0, 1920, 1040)];
        var coordinator = CreateCoordinator(native);
        var windows = new[] { Windows()[0] };
        var placements = new Dictionary<string, NormalizedClientWindowPlacement>
        {
            ["first"] = new()
            {
                MonitorDeviceName = @"\\.\DISPLAY1",
                MonitorIndex = 0,
                Left = 0.1,
                Top = 0.1,
                Width = 0.5,
                Height = 0.5
            }
        };

        var result = await coordinator.RestorePlacementsAsync(
            windows,
            placements,
            Preferences(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError ?? result.ZOrderError);
        Assert.Equal(
            new RobloxPixelRect(192, 104, 960, 520),
            native.RequestedBounds[(nint)100]);
        Assert.Equal(@"\\.\DISPLAY9", result.Items[0].Placement!.MonitorDeviceName);
        Assert.True(result.ZOrderApplied);
    }

    [Fact]
    public async Task RestorePlacementsAsync_ClampsToConfiguredClientMinimum()
    {
        var native = ReadyNative();
        native.Monitors =
        [Monitor(@"\\.\DISPLAY1", 0, true, -1000, -500, 1000, 800)];
        native.Windows[(nint)100].OuterBounds =
            new RobloxPixelRect(-900, -400, 816, 640);
        native.Windows[(nint)100].ClientBounds =
            new RobloxPixelRect(-892, -368, 800, 600);
        var coordinator = CreateCoordinator(native);
        var placements = new Dictionary<string, NormalizedClientWindowPlacement>
        {
            ["first"] = new()
            {
                MonitorDeviceName = @"\\.\DISPLAY1",
                MonitorIndex = 0,
                Left = 0.8,
                Top = 0.8,
                Width = 0.1,
                Height = 0.1
            }
        };

        var result = await coordinator.RestorePlacementsAsync(
            [Windows()[0]],
            placements,
            Preferences(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError ?? result.ZOrderError);
        // 640x480 minimum client plus the measured 16x40 frame. Position is
        // clamped so that the complete outer window remains in the work area.
        Assert.Equal(
            new RobloxPixelRect(-656, -220, 656, 520),
            native.RequestedBounds[(nint)100]);
    }

    [Fact]
    public async Task RestorePlacementsAsync_RefitsRobloxEnforcedSizeInsideWorkArea()
    {
        var native = ReadyNative();
        native.Monitors =
        [Monitor(@"\\.\DISPLAY1", 0, true, 0, 0, 1200, 800)];
        native.Windows[(nint)100].MinimumOuterSize =
            new RobloxPixelSize(900, 700);
        var coordinator = CreateCoordinator(native);
        var placements = new Dictionary<string, NormalizedClientWindowPlacement>
        {
            ["first"] = new()
            {
                MonitorDeviceName = @"\\.\DISPLAY1",
                MonitorIndex = 0,
                Left = 0.5,
                Top = 0.4,
                Width = 0.5,
                Height = 0.5
            }
        };

        var result = await coordinator.RestorePlacementsAsync(
            [Windows()[0]],
            placements,
            Preferences(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError ?? result.ZOrderError);
        Assert.Equal(
            new RobloxPixelRect(300, 100, 900, 700),
            native.RequestedBounds[(nint)100]);
        Assert.Equal(
            new RobloxPixelRect(300, 100, 900, 700),
            result.Items[0].RealizedBounds);
        Assert.True(result.Items[0].WasClamped);
    }

    [Fact]
    public async Task RestorePlacementsAsync_MapsAccountsIndependentlyOfWindowReadinessOrder()
    {
        var native = ReadyNative();
        var coordinator = CreateCoordinator(native);
        var placements = new Dictionary<string, NormalizedClientWindowPlacement>
        {
            ["first"] = new()
            {
                MonitorDeviceName = @"\\.\DISPLAY1",
                MonitorIndex = 0,
                Left = 0,
                Top = 0,
                Width = 0.4,
                Height = 0.5
            },
            ["second"] = new()
            {
                MonitorDeviceName = @"\\.\DISPLAY1",
                MonitorIndex = 0,
                Left = 0.6,
                Top = 0.5,
                Width = 0.4,
                Height = 0.5
            }
        };

        var result = await coordinator.RestorePlacementsAsync(
            Windows().Reverse().ToArray(),
            placements,
            Preferences(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError ?? result.ZOrderError);
        Assert.Equal(0, native.RequestedBounds[(nint)100].Left);
        Assert.Equal(1152, native.RequestedBounds[(nint)101].Left);
        Assert.Equal("second", result.Items[0].Key);
        Assert.Equal("first", result.Items[1].Key);
    }

    [Fact]
    public async Task RestorePlacementsAsync_UsesNamedNegativeMixedDpiMonitor()
    {
        var native = ReadyNative();
        native.Monitors =
        [
            Monitor(@"\\.\DISPLAY1", 1, true, 0, 0, 2560, 1400),
            new RobloxMonitor(
                @"\\.\DISPLAY2",
                0,
                false,
                new RobloxPixelRect(-1080, 338, 1080, 1920),
                new RobloxPixelRect(-1080, 338, 1080, 1920),
                144,
                144)
        ];
        var coordinator = CreateCoordinator(native);
        var placements = new Dictionary<string, NormalizedClientWindowPlacement>
        {
            ["first"] = new()
            {
                MonitorDeviceName = @"\\.\DISPLAY2",
                MonitorIndex = 0,
                Left = 0,
                Top = 0.1,
                Width = 0.8,
                Height = 0.5
            }
        };

        var result = await coordinator.RestorePlacementsAsync(
            [Windows()[0]],
            placements,
            Preferences(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.GlobalError ?? result.ZOrderError);
        Assert.Equal(
            new RobloxPixelRect(-1080, 530, 984, 960),
            native.RequestedBounds[(nint)100]);
        Assert.Equal(
            @"\\.\DISPLAY2",
            result.Items[0].Placement!.MonitorDeviceName);
    }

    [Fact]
    public async Task RestorePlacementsAsync_ReportsInvalidSlotWithoutBlockingOthers()
    {
        var native = ReadyNative();
        var coordinator = CreateCoordinator(native);
        var placements = new Dictionary<string, NormalizedClientWindowPlacement>
        {
            ["first"] = new()
            {
                MonitorDeviceName = @"\\.\DISPLAY1",
                MonitorIndex = 0,
                Left = 0,
                Top = 0,
                Width = 0.5,
                Height = 0.5
            },
            ["second"] = new()
            {
                MonitorIndex = 0,
                Left = double.NaN,
                Top = 0,
                Width = 0.5,
                Height = 0.5
            }
        };

        var result = await coordinator.RestorePlacementsAsync(
            Windows(),
            placements,
            Preferences(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.HasPartialFailures);
        Assert.True(result.Items[0].Success);
        Assert.Equal(
            RobloxSessionLayoutStage.InputValidation,
            result.Items[1].FailureStage);
        Assert.False(native.RequestedBounds.ContainsKey((nint)101));
        Assert.Empty(native.AppliedZOrderPlacements);
    }

    private static RobloxSessionLayoutCoordinator CreateCoordinator(
        CoordinatorNative native) =>
        new(
            new RobloxWindowService(
                native,
                TimeSpan.FromMilliseconds(1)),
            CreateShareableTrustContext);

    private static RobloxExecutableTrustContext
        CreateShareableTrustContext() =>
        new(_ => File.OpenHandle(
            typeof(RobloxSessionLayoutCoordinatorTests).Assembly.Location,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess));

    private static RobloxSessionLayoutWindow[] Windows() =>
    [
        new("first", FirstIdentity, (nint)100),
        new("second", SecondIdentity, (nint)101)
    ];

    private static TemplatePreferences Preferences() => new()
    {
        TargetWidth = 800,
        TargetHeight = 600,
        MinimumWidth = 640,
        MinimumHeight = 480,
        RevealX = 56,
        RevealY = 36
    };

    private static CoordinatorNative ReadyNative()
    {
        var native = new CoordinatorNative
        {
            Monitors =
            [Monitor(@"\\.\DISPLAY1", 0, true, 0, 0, 1920, 1080)]
        };
        native.Windows[(nint)100] = State(
            FirstIdentity.ProcessId,
            new RobloxPixelRect(10, 20, 816, 640));
        native.Windows[(nint)101] = State(
            SecondIdentity.ProcessId,
            new RobloxPixelRect(30, 40, 816, 640));
        native.AcceptedIdentities.Add(FirstIdentity);
        native.AcceptedIdentities.Add(SecondIdentity);
        return native;
    }

    private static (CoordinatorNative Native,
        RobloxSessionLayoutWindow[] Windows) ReadyNative(int targetCount)
    {
        var native = new CoordinatorNative
        {
            Monitors =
            [Monitor(@"\\.\DISPLAY1", 0, true, 0, 0, 1920, 1080)]
        };
        var windows = Enumerable.Range(0, targetCount)
            .Select(index =>
            {
                var identity = FirstIdentity with
                {
                    ProcessId = FirstIdentity.ProcessId + index,
                    StartTimeUtc = FirstIdentity.StartTimeUtc.AddSeconds(index)
                };
                var handle = (nint)(100 + index);
                native.Windows[handle] = State(
                    identity.ProcessId,
                    new RobloxPixelRect(10, 20, 816, 640));
                native.AcceptedIdentities.Add(identity);
                return new RobloxSessionLayoutWindow(
                    $"account-{index}",
                    identity,
                    handle);
            })
            .ToArray();
        return (native, windows);
    }

    private static void AssertOneForcedTrustRefresh(CoordinatorNative native)
    {
        Assert.NotEmpty(native.ForceTrustRefreshCalls);
        Assert.Equal(
            1,
            native.ForceTrustRefreshCalls.Count(forceRefresh =>
                forceRefresh));
        var forcedIndex = native.ForceTrustRefreshCalls.FindIndex(
            forceRefresh => forceRefresh);
        Assert.NotNull(native.ExecutableTrustHandles[forcedIndex]);
    }

    private static CoordinatorWindowState State(
        int processId,
        RobloxPixelRect outerBounds) => new()
        {
            ProcessId = processId,
            OuterBounds = outerBounds,
            ClientBounds = new RobloxPixelRect(
                outerBounds.Left + 8,
                outerBounds.Top + 32,
                outerBounds.Width - 16,
                outerBounds.Height - 40)
        };

    private static RobloxMonitor Monitor(
        string deviceName,
        int index,
        bool primary,
        int left,
        int top,
        int width,
        int height,
        string? stableId = null) =>
        new(
            deviceName,
            index,
            primary,
            new RobloxPixelRect(left, top, width, height),
            new RobloxPixelRect(left, top, width, height),
            96,
            96)
        {
            StableId = stableId
        };

    private sealed class CoordinatorWindowState
    {
        internal int ProcessId { get; init; }
        internal RobloxPixelRect OuterBounds { get; set; }
        internal RobloxPixelRect ClientBounds { get; set; }
        internal RobloxPixelSize MinimumOuterSize { get; set; }
        internal bool AllowSetBounds { get; set; } = true;
        internal bool Usable { get; set; } = true;
        internal bool Minimized { get; set; }
    }

    private sealed class CoordinatorNative : IRobloxWindowNativeAdapter
    {
        internal Dictionary<nint, CoordinatorWindowState> Windows { get; } = [];
        internal HashSet<RobloxClientProcessIdentity> AcceptedIdentities { get; } = [];
        internal Dictionary<nint, RobloxPixelRect> RequestedBounds { get; } = [];
        internal List<RobloxWindowZOrderPlacement> AppliedZOrderPlacements
        { get; } = [];
        internal IReadOnlyList<RobloxMonitor> Monitors { get; set; } = [];
        internal List<bool> ForceTrustRefreshCalls { get; } = [];
        internal List<Microsoft.Win32.SafeHandles.SafeFileHandle?>
            ExecutableTrustHandles
        { get; } = [];

        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

        public RobloxProcessVerificationStatus VerifyProcess(
            RobloxClientProcessIdentity identity,
            bool forceTrustRefresh,
            bool verifyExecutableTrust,
            Microsoft.Win32.SafeHandles.SafeFileHandle?
                executableTrustHandle)
        {
            ForceTrustRefreshCalls.Add(forceTrustRefresh);
            ExecutableTrustHandles.Add(executableTrustHandle);
            _ = verifyExecutableTrust;
            return AcceptedIdentities.Contains(identity)
                ? RobloxProcessVerificationStatus.Verified
                : RobloxProcessVerificationStatus.StartTimeMismatch;
        }

        public IReadOnlyList<nint> EnumerateTopLevelWindows(int processId) =>
            Windows
                .Where(item => item.Value.ProcessId == processId)
                .Select(item => item.Key)
                .ToArray();

        public IReadOnlyList<nint> EnumerateTopLevelWindowsInZOrder() =>
            Windows.Keys.ToArray();

        public bool IsUsableTopLevelWindow(nint windowHandle) =>
            Windows.TryGetValue(windowHandle, out var state) && state.Usable;

        public int GetWindowProcessId(nint windowHandle) =>
            Windows.TryGetValue(windowHandle, out var state)
                ? state.ProcessId
                : 0;

        public bool IsMinimized(nint windowHandle) =>
            Windows.TryGetValue(windowHandle, out var state) && state.Minimized;

        public bool IsMaximized(nint windowHandle) => false;

        public bool IsFullscreen(nint windowHandle) => false;

        public bool TryRestore(nint windowHandle) =>
            Windows.ContainsKey(windowHandle);

        public bool TryGetGeometry(
            nint windowHandle,
            out RobloxPixelRect outerBounds,
            out RobloxPixelRect clientBounds)
        {
            if (!Windows.TryGetValue(windowHandle, out var state))
            {
                outerBounds = default;
                clientBounds = default;
                return false;
            }

            outerBounds = state.OuterBounds;
            clientBounds = state.ClientBounds;
            return outerBounds.IsValid && clientBounds.IsValid;
        }

        public bool TrySetBounds(
            nint windowHandle,
            RobloxPixelRect outerBounds)
        {
            if (!Windows.TryGetValue(windowHandle, out var state) ||
                !state.AllowSetBounds)
            {
                return false;
            }

            RequestedBounds[windowHandle] = outerBounds;
            var realizedWidth = state.MinimumOuterSize.IsValid
                ? Math.Max(outerBounds.Width, state.MinimumOuterSize.Width)
                : outerBounds.Width;
            var realizedHeight = state.MinimumOuterSize.IsValid
                ? Math.Max(outerBounds.Height, state.MinimumOuterSize.Height)
                : outerBounds.Height;
            state.OuterBounds = outerBounds with
            {
                Width = realizedWidth,
                Height = realizedHeight
            };
            state.ClientBounds = new RobloxPixelRect(
                state.OuterBounds.Left + 8,
                state.OuterBounds.Top + 32,
                state.OuterBounds.Width - 16,
                state.OuterBounds.Height - 40);
            return true;
        }

        public bool IsTopmost(nint windowHandle) => false;

        public bool TryDemoteTopmostWithoutActivation(nint windowHandle) =>
            Windows.ContainsKey(windowHandle);

        public bool TryApplyZOrderWithoutActivation(
            IReadOnlyList<RobloxWindowZOrderPlacement> placements)
        {
            AppliedZOrderPlacements.AddRange(placements);
            return placements.All(placement =>
                Windows.ContainsKey(placement.Handle));
        }

        public bool TrySetForeground(nint windowHandle) => false;

        public nint GetForegroundWindow() => nint.Zero;

        public nint GetRootWindowAtPoint(int x, int y)
        {
            _ = x;
            _ = y;
            return nint.Zero;
        }

        public IReadOnlyList<RobloxMonitor> GetMonitors() => Monitors;

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }
}
