using System.Diagnostics;
using SessionDock.ExactWheel;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RobloxWindowServiceTests
{
    private static readonly RobloxClientProcessIdentity Identity = new(
        42,
        new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc),
        @"C:\Roblox\Versions\version-test\RobloxPlayerBeta.exe");

    [Fact]
    public async Task WaitForWindowAsync_UsesExactVerifiedProcessAndRefreshesTrust()
    {
        var native = ReadyNative();
        var service = CreateService(native);

        var result = await service.WaitForWindowAsync(
            Identity,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal((nint)100, result.Window!.Handle);
        Assert.Equal(Identity, result.Window.Identity);
        Assert.True(native.EnumerationCount >= 3);
        Assert.Empty(native.ForceTrustRefreshCalls);
        Assert.Equal([true], native.PinForceTrustRefreshCalls);
        Assert.Equal(
            [false],
            Assert.Single(native.LifetimePins).ForceTrustRefreshCalls);
    }

    [Fact]
    public async Task WaitForWindowAsync_WaitsForHandleAndGeometryToSettle()
    {
        var native = ReadyNative();
        native.WindowEnumerationSequence.Enqueue([(nint)200]);
        native.WindowEnumerationSequence.Enqueue([(nint)100]);
        native.WindowProcessIds[(nint)200] = Identity.ProcessId;
        native.OuterBoundsByWindow[(nint)200] =
            new RobloxPixelRect(0, 0, 320, 200);
        native.ClientBoundsByWindow[(nint)200] =
            new RobloxPixelRect(8, 32, 304, 160);
        native.GeometrySequence.Enqueue((
            new RobloxPixelRect(10, 20, 640, 480),
            new RobloxPixelRect(18, 52, 624, 440)));
        native.GeometrySequence.Enqueue((
            new RobloxPixelRect(10, 20, 800, 600),
            new RobloxPixelRect(18, 52, 784, 560)));
        native.GeometrySequence.Enqueue((
            new RobloxPixelRect(10, 20, 816, 640),
            new RobloxPixelRect(18, 52, 800, 600)));
        var service = CreateService(native);

        var result = await service.WaitForWindowAsync(
            Identity,
            TimeSpan.FromMilliseconds(80),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal((nint)100, result.Window!.Handle);
        Assert.Equal(
            new RobloxPixelRect(10, 20, 816, 640),
            result.Window.OuterBounds);
        Assert.True(native.EnumerationCount >= 6);
    }

    [Fact]
    public async Task WaitForWindowAsync_EqualMainCandidatesFailSafely()
    {
        var native = ReadyNative();
        native.Windows = [(nint)100, (nint)101];
        native.WindowProcessIds[(nint)101] = Identity.ProcessId;
        native.OuterBoundsByWindow[(nint)101] = native.OuterBounds;
        native.ClientBoundsByWindow[(nint)101] = native.ClientBounds;
        var service = CreateService(native);

        var result = await service.WaitForWindowAsync(
            Identity,
            TimeSpan.FromMilliseconds(40),
            TestContext.Current.CancellationToken);

        Assert.Equal(RobloxWindowOperationStatus.TimedOut, result.Status);
        Assert.Contains("equally viable", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(true, native.ForceTrustRefreshCalls);
    }

    [Fact]
    public async Task WaitForWindowAsync_RejectsIdentityBeforeEnumeratingWindows()
    {
        var native = ReadyNative();
        native.VerificationStatus =
            RobloxProcessVerificationStatus.StartTimeMismatch;
        var service = CreateService(native);

        var result = await service.WaitForWindowAsync(
            Identity,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            RobloxWindowOperationStatus.IdentityRejected,
            result.Status);
        Assert.Equal(0, native.EnumerationCount);
    }

    [Fact]
    public async Task WaitForWindowAsync_NeverAdoptsWindowFromAnotherPid()
    {
        var native = ReadyNative();
        native.WindowProcessId = 99;
        var service = CreateService(native);

        var result = await service.WaitForWindowAsync(
            Identity,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(RobloxWindowOperationStatus.TimedOut, result.Status);
        Assert.Null(result.Window);
    }

    [Fact]
    public async Task WaitForWindowAsync_ReportsFullscreenExplicitly()
    {
        var native = ReadyNative();
        native.Fullscreen = true;
        var service = CreateService(native);

        var result = await service.WaitForWindowAsync(
            Identity,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(RobloxWindowOperationStatus.Fullscreen, result.Status);
        Assert.Contains("fullscreen", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WaitForWindowAsync_DoesNotTreatMinimizedGeometryAsReady()
    {
        var native = ReadyNative();
        native.Minimized = true;
        native.OuterBounds = new RobloxPixelRect(-21_333, -21_333, 160, 30);
        native.ClientBounds = new RobloxPixelRect(-21_333, -21_333, 150, 20);
        var service = CreateService(native);

        var result = await service.WaitForWindowAsync(
            Identity,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(RobloxWindowOperationStatus.TimedOut, result.Status);
        Assert.Null(result.Window);
    }

    [Fact]
    public async Task SetBoundsAsync_RestoresAndReturnsRobloxRealizedClamp()
    {
        var native = ReadyNative();
        native.Minimized = true;
        native.RealizedBoundsAfterSet =
            new RobloxPixelRect(20, 30, 640, 480);
        native.RealizedClientAfterSet =
            new RobloxPixelRect(28, 62, 624, 440);
        var service = CreateService(native);
        var requested = new RobloxPixelRect(20, 30, 500, 400);

        var result = await service.SetBoundsAsync(
            Identity,
            (nint)100,
            requested,
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.True(native.RestoreCalled);
        Assert.False(native.SetForegroundCalled);
        Assert.Equal(requested, native.LastRequestedBounds);
        Assert.Equal(
            new RobloxPixelRect(20, 30, 640, 480),
            result.Window!.OuterBounds);
        Assert.True(result.WasClamped);
        Assert.Equal([true, false], native.ForceTrustRefreshCalls);
    }

    [Fact]
    public async Task SetBoundsAsync_ReappliesAfterLateStartupReposition()
    {
        var native = ReadyNative();
        var requested = new RobloxPixelRect(-900, 400, 700, 520);
        var requestedClient = new RobloxPixelRect(-892, 432, 684, 480);
        var startupPosition = new RobloxPixelRect(300, 200, 700, 520);
        var startupClient = new RobloxPixelRect(308, 232, 684, 480);
        for (var index = 0; index < 3; index++)
            native.GeometrySequence.Enqueue((requested, requestedClient));
        for (var index = 0; index < 51; index++)
            native.GeometrySequence.Enqueue((startupPosition, startupClient));
        var service = CreateService(native);

        var result = await service.SetBoundsAsync(
            Identity,
            (nint)100,
            requested,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(requested, result.Window!.OuterBounds);
        Assert.Equal(2, native.SetBoundsCallCount);
    }

    [Fact]
    public async Task SetBoundsAsync_FailsBeforeMoveWhenWindowOwnershipChanged()
    {
        var native = ReadyNative();
        native.WindowProcessId = 777;
        var service = CreateService(native);

        var result = await service.SetBoundsAsync(
            Identity,
            (nint)100,
            new RobloxPixelRect(0, 0, 800, 600),
            realizeTimeout: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            RobloxWindowOperationStatus.IdentityRejected,
            result.Status);
        Assert.False(native.SetBoundsCalled);
    }

    [Fact]
    public async Task FocusAsync_FailsClosedWhenWindowsDeniesForeground()
    {
        var native = ReadyNative();
        native.SetForegroundResult = true;
        native.ForegroundWindow = (nint)200;
        var service = CreateService(native);

        var result = await service.FocusAsync(
            Identity,
            (nint)100,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(RobloxWindowOperationStatus.FocusDenied, result.Status);
        Assert.True(native.SetForegroundCalled);
        Assert.Contains("denied", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FocusAsync_RequiresExactForegroundHwndAndPid()
    {
        var native = ReadyNative();
        native.SetForegroundResult = true;
        native.SetForegroundChangesForeground = true;
        var service = CreateService(native);

        var result = await service.FocusAsync(
            Identity,
            (nint)100,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal((nint)100, result.Window!.Handle);
        Assert.Equal(Identity.ProcessId, native.WindowProcessId);
        Assert.Equal([true, true], native.ForceTrustRefreshCalls);
    }

    [Fact]
    public async Task FocusAsync_WithRetainedLease_ReusesFreshIdentityProof()
    {
        var native = ReadyNative();
        native.SetForegroundResult = true;
        native.SetForegroundChangesForeground = true;
        var service = CreateService(native);
        var acquisition = service.AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(
            acquisition.Lease);
        var pin = Assert.Single(native.LifetimePins);
        native.ForceTrustRefreshCalls.Clear();
        native.WindowProcessIdReads.Clear();
        var livenessChecksBefore = pin.RetainedLivenessCheckCount;
        var usabilityChecksBefore = native.UsableReadCount;

        var result = await service.FocusAsync(
            lease,
            Identity,
            (nint)100,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Empty(native.ForceTrustRefreshCalls);
        Assert.Empty(pin.ForceTrustRefreshCalls);
        Assert.Equal(0, pin.VerificationCount);
        Assert.Equal(
            3,
            pin.RetainedLivenessCheckCount - livenessChecksBefore);
        Assert.Equal(
            [(nint)100, (nint)100, (nint)100, (nint)100],
            native.WindowProcessIdReads);
        Assert.Equal(3, native.UsableReadCount - usabilityChecksBefore);
    }

    [Fact]
    public async Task FocusAsync_WithRetainedLease_RejectsMismatchedExactTarget()
    {
        var native = ReadyNative();
        var service = CreateService(native);
        var acquisition = service.AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(
            acquisition.Lease);

        var result = await service.FocusAsync(
            lease,
            Identity with { ProcessId = 43 },
            (nint)100,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            RobloxWindowOperationStatus.IdentityRejected,
            result.Status);
        Assert.False(native.SetForegroundCalled);
        Assert.Null(lease.Failure);
    }

    [Fact]
    public async Task FocusAsync_WithRetainedLease_FailsClosedOnTokenRevalidation()
    {
        var native = ReadyNative();
        native.SetForegroundResult = true;
        native.SetForegroundChangesForeground = true;
        var service = CreateService(native);
        var acquisition = service.AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(
            acquisition.Lease);
        Assert.Single(native.LifetimePins).VerificationStatus =
            RobloxProcessVerificationStatus.WrongUserOrSession;
        native.Advance(TimeSpan.FromSeconds(6));

        var result = await service.FocusAsync(
            lease,
            Identity,
            (nint)100,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            RobloxWindowOperationStatus.IdentityRejected,
            result.Status);
        Assert.NotNull(lease.Failure);
        Assert.Empty(native.ForceTrustRefreshCalls);
    }

    [Fact]
    public void SessionMacroLeaseCache_ReusesOneExactLeaseAcrossCycles()
    {
        var native = ReadyNative();
        var service = CreateService(native);
        var window = new RobloxSessionLayoutWindow(
            "account-a",
            Identity,
            (nint)100);
        var cache = new SessionMacroPlaybackLeaseCache();

        var first = cache.GetOrAcquire(service, window);
        var repeated = cache.GetOrAcquire(service, window);

        Assert.True(first.Success, first.Failure?.Error);
        Assert.Same(first.Lease, repeated.Lease);
        Assert.Equal(1, cache.Count);
        Assert.Equal([true], native.PinForceTrustRefreshCalls);
        var pin = Assert.Single(native.LifetimePins);
        Assert.Equal(0, pin.DisposeCount);

        cache.Dispose();

        Assert.Equal(1, pin.DisposeCount);
    }

    [Fact]
    public void SessionMacroLeaseCache_CachedSingleTargetHitsAreAllocationFree()
    {
        var native = ReadyNative();
        var service = CreateService(native);
        var window = new RobloxSessionLayoutWindow(
            "account-a",
            Identity,
            (nint)100);
        using var cache = new SessionMacroPlaybackLeaseCache();
        _ = cache.GetOrAcquire(service, window);
        for (var index = 0; index < 100; index++)
            _ = cache.GetOrAcquire(service, window);
        var aggregate = 0;
        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            aggregate = 0;
            for (var index = 0; index < 10_000; index++)
            {
                aggregate += cache.GetOrAcquire(service, window)
                    .Lease?.AllowedTargetCount ?? 0;
            }
        });
        Assert.Equal(10_000, aggregate);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SessionMacroLeaseCache_EightTargetHitsAreAllocationFree()
    {
        var identities = Enumerable.Range(0, 8)
            .Select(index => Identity with
            {
                ProcessId = Identity.ProcessId + index,
                StartTimeUtc = Identity.StartTimeUtc.AddSeconds(index)
            })
            .ToArray();
        var windows = identities
            .Select((identity, index) => new RobloxSessionLayoutWindow(
                $"account-{index}",
                identity,
                (nint)(100 + index)))
            .ToArray();
        var reversed = windows.Reverse().ToArray();
        var native = ReadyNative();
        native.AcceptedIdentities = identities;
        foreach (var window in windows)
            native.WindowProcessIds[window.Handle] = window.Identity.ProcessId;
        var service = CreateService(native);
        using var cache = new SessionMacroPlaybackLeaseCache();
        var first = cache.GetOrAcquire(service, windows);
        Assert.True(first.Success, first.Failure?.Error);
        for (var index = 0; index < 100; index++)
            _ = cache.GetOrAcquire(service, reversed);
        var aggregate = 0;
        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            aggregate = 0;
            for (var index = 0; index < 10_000; index++)
            {
                aggregate += cache.GetOrAcquire(service, reversed)
                    .Lease?.AllowedTargetCount ?? 0;
            }
        });
        Assert.Equal(80_000, aggregate);
        Assert.Equal(8, native.PinAttemptCount);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void SessionMacroLeaseCache_CachesClassOnlyForExactRetainedTarget()
    {
        var captureCalls = 0;
        using var cache = new SessionMacroPlaybackLeaseCache(windowHandle =>
        {
            captureCalls++;
            Assert.Equal((nint)100, windowHandle);
            return "WINDOWSCLIENT";
        });
        var native = ReadyNative();
        var service = CreateService(native);
        var window = new RobloxSessionLayoutWindow(
            "account-a",
            Identity,
            (nint)100);

        Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrCaptureWindowClass(window));
        var acquired = cache.GetOrAcquire(service, window);
        Assert.True(acquired.Success, acquired.Failure?.Error);

        var first = cache.GetOrCaptureWindowClass(window);
        var repeated = cache.GetOrCaptureWindowClass(window);
        for (var index = 0; index < 100; index++)
            _ = cache.GetOrCaptureWindowClass(window);
        var aggregate = 0;
        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            aggregate = 0;
            for (var index = 0; index < 10_000; index++)
                aggregate += cache.GetOrCaptureWindowClass(window).Length;
        });
        Assert.Same(first, repeated);
        Assert.Equal(13, first.Length);
        Assert.Equal(130_000, aggregate);
        Assert.Equal(1, captureCalls);
        Assert.Equal(1, cache.CachedWindowClassCount);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task FocusAsync_ReportsFullscreenWithoutForegroundAttempt()
    {
        var native = ReadyNative();
        native.Fullscreen = true;
        var service = CreateService(native);

        var result = await service.FocusAsync(
            Identity,
            (nint)100,
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.Equal(RobloxWindowOperationStatus.Fullscreen, result.Status);
        Assert.False(native.SetForegroundCalled);
    }

    [Fact]
    public async Task ApplyZOrderAsync_PreservesUnrelatedSlotsWhileOrderingClients()
    {
        var native = ReadyNative();
        native.WindowProcessIds[(nint)100] = 42;
        native.WindowProcessIds[(nint)101] = 43;
        native.WindowProcessIds[(nint)102] = 44;
        var identities = new[]
        {
            Identity,
            Identity with { ProcessId = 43 },
            Identity with { ProcessId = 44 }
        };
        native.AcceptedIdentities = identities;
        native.TopLevelZOrder =
        [
            (nint)900,
            (nint)100,
            (nint)901,
            (nint)101,
            (nint)902,
            (nint)102,
            (nint)903
        ];
        var monitor = new RobloxMonitor(
            @"\\.\DISPLAY1",
            0,
            true,
            new RobloxPixelRect(0, 0, 1920, 1080),
            new RobloxPixelRect(0, 0, 1920, 1080),
            96,
            96);
        var plan = new RobloxCascadeLayoutPlan(
            true,
            [
                new RobloxCascadePlacement(
                    "first", monitor, new RobloxPixelRect(0, 0, 800, 600),
                    0, 0, 0),
                new RobloxCascadePlacement(
                    "second", monitor, new RobloxPixelRect(50, 50, 800, 600),
                    0, 1, 1),
                new RobloxCascadePlacement(
                    "third", monitor, new RobloxPixelRect(100, 100, 800, 600),
                    0, 2, 2)
            ],
            1,
            null);
        var service = CreateService(native);

        var result = await service.ApplyZOrderAsync(
            plan,
            [
                new RobloxWindowZOrderTarget("third", identities[2], (nint)102),
                new RobloxWindowZOrderTarget("first", identities[0], (nint)100),
                new RobloxWindowZOrderTarget("second", identities[1], (nint)101)
            ],
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal(
            [
                new RobloxWindowZOrderPlacement((nint)102, (nint)900),
                new RobloxWindowZOrderPlacement((nint)101, (nint)901),
                new RobloxWindowZOrderPlacement((nint)100, (nint)902)
            ],
            native.AppliedZOrderPlacements);
        Assert.Empty(native.DemotedWindows);
        Assert.False(native.SetForegroundCalled);
    }

    [Fact]
    public async Task ApplyZOrderAsync_ValidatesEveryIdentityBeforeAnyMutation()
    {
        var native = ReadyNative();
        native.VerificationStatus =
            RobloxProcessVerificationStatus.ExecutableNotTrusted;
        var service = CreateService(native);
        var monitor = new RobloxMonitor(
            @"\\.\DISPLAY1",
            0,
            true,
            new RobloxPixelRect(0, 0, 1920, 1080),
            new RobloxPixelRect(0, 0, 1920, 1080),
            96,
            96);
        var plan = new RobloxCascadeLayoutPlan(
            true,
            [new RobloxCascadePlacement(
                "first", monitor, new RobloxPixelRect(0, 0, 800, 600),
                0, 0, 0)],
            1,
            null);

        var result = await service.ApplyZOrderAsync(
            plan,
            [new RobloxWindowZOrderTarget("first", Identity, (nint)100)],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Empty(native.AppliedZOrderPlacements);
        Assert.Empty(native.DemotedWindows);
    }

    [Fact]
    public async Task ApplyZOrderAsync_DemotesLegacyTopmostWithoutRaisingSingleClient()
    {
        var native = ReadyNative();
        native.TopLevelZOrder = [(nint)700, (nint)100, (nint)701];
        native.TopmostWindows.Add((nint)100);
        var monitor = new RobloxMonitor(
            @"\\.\DISPLAY1",
            0,
            true,
            new RobloxPixelRect(0, 0, 1920, 1080),
            new RobloxPixelRect(0, 0, 1920, 1080),
            96,
            96);
        var plan = new RobloxCascadeLayoutPlan(
            true,
            [new RobloxCascadePlacement(
                "first", monitor, new RobloxPixelRect(0, 0, 800, 600),
                0, 0, 0)],
            1,
            null);
        var service = CreateService(native);

        var result = await service.ApplyZOrderAsync(
            plan,
            [new RobloxWindowZOrderTarget("first", Identity, (nint)100)],
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal([(nint)100], native.DemotedWindows);
        Assert.Empty(native.AppliedZOrderPlacements);
        Assert.DoesNotContain((nint)100, native.TopmostWindows);
    }

    [Fact]
    public void Win32Adapter_UsesNonActivatingNonTopmostArrangementPrimitives()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "Services",
            "RobloxWindowService.cs"));

        Assert.DoesNotContain("new nint(-1)", source);
        Assert.DoesNotContain("ShowRestore", source);
        Assert.Contains("ShowNormalNoActivate = 4", source);
        Assert.Contains("WindowNotTopmost = new(-2)", source);
        Assert.Contains("BeginDeferWindowPos", source);
        Assert.Contains("SWP_NOACTIVATE", source);
        Assert.Contains("process.Exited += Process_Exited", source);
        Assert.Contains("process.Exited -= Process_Exited", source);
        Assert.Contains("WaitForSingleObject", source);
        Assert.Contains("IsRetainedProcessAlive", source);
        Assert.Contains(
            "ClientToScreen(windowHandle, ref topLeft)",
            source);
        Assert.Contains(
            "ClientToScreen(windowHandle, ref bottomRight)",
            source);
    }

    [Fact]
    public void Win32Adapter_CoalescesConcurrentPerProcessWindowEnumeration()
    {
        var now = new DateTimeOffset(
            2026,
            8,
            4,
            12,
            0,
            0,
            TimeSpan.Zero);
        var enumerationCount = 0;
        IReadOnlyDictionary<int, IReadOnlyList<nint>> Enumerate()
        {
            enumerationCount++;
            return new Dictionary<int, IReadOnlyList<nint>>
            {
                [42] = [(nint)100],
                [43] = [(nint)101]
            };
        }
        var adapter = new Win32RobloxWindowNativeAdapter(
            () => now,
            Enumerate);

        Assert.Equal([(nint)100], adapter.EnumerateTopLevelWindows(42));
        Assert.Equal([(nint)101], adapter.EnumerateTopLevelWindows(43));
        Assert.Empty(adapter.EnumerateTopLevelWindows(44));
        Assert.Equal(1, enumerationCount);

        now += TimeSpan.FromMilliseconds(100);

        Assert.Equal([(nint)100], adapter.EnumerateTopLevelWindows(42));
        Assert.Equal(2, enumerationCount);
    }

    [Fact]
    public void Win32Adapter_InvalidatesWindowSnapshotWhenClockRegresses()
    {
        var now = new DateTimeOffset(
            2026,
            8,
            4,
            12,
            0,
            0,
            TimeSpan.Zero);
        var enumerationCount = 0;
        var adapter = new Win32RobloxWindowNativeAdapter(
            () => now,
            () =>
            {
                enumerationCount++;
                return new Dictionary<int, IReadOnlyList<nint>>();
            });
        _ = adapter.EnumerateTopLevelWindows(42);

        now -= TimeSpan.FromSeconds(1);

        _ = adapter.EnumerateTopLevelWindows(42);
        Assert.Equal(2, enumerationCount);
    }

    [Fact]
    public void AcquirePlaybackTargetLease_RejectsConflictingMappingsBeforePinning()
    {
        var otherIdentity = Identity with { ProcessId = 43 };
        IReadOnlyList<RobloxPlaybackTarget>[] conflictingSets =
        [
            [
                new RobloxPlaybackTarget(Identity, (nint)100),
                new RobloxPlaybackTarget(Identity, (nint)101)
            ],
            [
                new RobloxPlaybackTarget(Identity, (nint)100),
                new RobloxPlaybackTarget(otherIdentity, (nint)100)
            ]
        ];

        foreach (var targets in conflictingSets)
        {
            var native = ReadyNative();
            native.AcceptedIdentities = [Identity, otherIdentity];
            var result = CreateService(native).AcquirePlaybackTargetLease(
                targets);

            Assert.False(result.Success);
            Assert.Equal(
                RobloxPlaybackTargetLeaseFailureKind.ConflictingTargetSet,
                result.Failure?.Kind);
            Assert.Equal(0, native.PinAttemptCount);
            Assert.Empty(native.LifetimePins);
        }
    }

    [Fact]
    public void AcquirePlaybackTargetLease_ForceValidatesWholeSessionAllowedSet()
    {
        var secondIdentity = Identity with
        {
            ProcessId = 43,
            StartTimeUtc = Identity.StartTimeUtc.AddSeconds(1)
        };
        var native = ReadyNative();
        native.AcceptedIdentities = [Identity, secondIdentity];
        native.WindowProcessIds[(nint)100] = Identity.ProcessId;
        native.WindowProcessIds[(nint)101] = secondIdentity.ProcessId;
        native.ForegroundWindow = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            [
                new RobloxPlaybackTarget(Identity, (nint)100),
                new RobloxPlaybackTarget(secondIdentity, (nint)101)
            ],
            TimeSpan.FromSeconds(1));

        Assert.True(result.Success, result.Failure?.Error);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        Assert.Equal(2, lease.AllowedTargetCount);
        Assert.Equal([true, true], native.PinForceTrustRefreshCalls);
        Assert.Equal(2, native.LifetimePins.Count);
        native.WindowProcessIdReads.Clear();
        Assert.True(lease.IsDispatchAuthorized());
        Assert.Equal([(nint)100], native.WindowProcessIdReads);

        native.ForegroundWindow = (nint)101;
        native.WindowProcessIdReads.Clear();

        Assert.True(lease.IsDispatchAuthorized());
        Assert.Equal([(nint)101], native.WindowProcessIdReads);
    }

    [Fact]
    public void PlaybackTargetLease_ClockRollbackInvalidatesInactiveTargetDeadline()
    {
        var secondIdentity = Identity with
        {
            ProcessId = 43,
            StartTimeUtc = Identity.StartTimeUtc.AddSeconds(1)
        };
        var native = ReadyNative();
        native.AcceptedIdentities = [Identity, secondIdentity];
        native.WindowProcessIds[(nint)100] = Identity.ProcessId;
        native.WindowProcessIds[(nint)101] = secondIdentity.ProcessId;
        native.ForegroundWindow = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            [
                new RobloxPlaybackTarget(Identity, (nint)100),
                new RobloxPlaybackTarget(secondIdentity, (nint)101)
            ],
            TimeSpan.FromSeconds(5));
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        var firstPin = native.LifetimePins[0];
        var secondPin = native.LifetimePins[1];

        native.Advance(TimeSpan.FromSeconds(-10));
        Assert.Null(lease.ValidateExactTarget(
            Identity,
            (nint)100,
            revalidateIdentityAndToken: false));
        Assert.Equal(1, firstPin.VerificationCount);
        Assert.Equal(0, secondPin.VerificationCount);

        native.ForegroundWindow = (nint)101;
        Assert.True(lease.IsDispatchAuthorized());
        Assert.Equal(1, secondPin.VerificationCount);
    }

    [Fact]
    public void AcquirePlaybackTargetLease_RejectsWrongExactWindowOwner()
    {
        var native = ReadyNative();
        native.WindowProcessId = 777;

        var result = CreateService(native).AcquirePlaybackTargetLease(
            Identity,
            (nint)100);

        Assert.False(result.Success);
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.WindowOwnershipChanged,
            result.Failure?.Kind);
        Assert.Equal(1, Assert.Single(native.LifetimePins).DisposeCount);
    }

    [Fact]
    public void PlaybackTargetLease_WindowOwnerFailureIsSticky()
    {
        var native = ReadyNative();
        native.ForegroundWindow = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        Assert.True(lease.IsDispatchAuthorized());

        native.WindowProcessId = 777;

        Assert.False(lease.TryAuthorizeDispatch(out var firstFailure));
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.WindowOwnershipChanged,
            firstFailure?.Kind);
        Assert.Equal(
            ExactWheelDispatchAuthorization.Denied,
            lease.GetDispatchAuthorization());
        native.WindowProcessId = Identity.ProcessId;
        Assert.False(lease.TryAuthorizeDispatch(out var repeatedFailure));
        Assert.Same(firstFailure, repeatedFailure);
    }

    [Fact]
    public void PlaybackTargetLease_RejectsAllowedHwndReusedByOtherProcess()
    {
        var secondIdentity = Identity with
        {
            ProcessId = 43,
            StartTimeUtc = Identity.StartTimeUtc.AddSeconds(1)
        };
        var native = ReadyNative();
        native.AcceptedIdentities = [Identity, secondIdentity];
        native.WindowProcessIds[(nint)100] = Identity.ProcessId;
        native.WindowProcessIds[(nint)101] = secondIdentity.ProcessId;
        native.ForegroundWindow = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            [
                new RobloxPlaybackTarget(Identity, (nint)100),
                new RobloxPlaybackTarget(secondIdentity, (nint)101)
            ]);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        native.WindowProcessIds[(nint)100] = secondIdentity.ProcessId;

        Assert.False(lease.TryAuthorizeDispatch(out var failure));
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.WindowOwnershipChanged,
            failure?.Kind);
        Assert.All(
            native.LifetimePins,
            pin => Assert.Equal(0, pin.VerificationCount));
        Assert.All(
            native.LifetimePins,
            pin => Assert.Equal(1, pin.DisposeCount));
    }

    [Fact]
    public void PlaybackTargetLease_TransientForegroundGapCanRecover()
    {
        var native = ReadyNative();
        native.ForegroundWindow = (nint)200;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);

        Assert.False(lease.TryAuthorizeDispatch(out var firstFailure));
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.ForegroundMismatch,
            firstFailure?.Kind);
        Assert.Equal(
            ExactWheelDispatchAuthorization.TemporarilyUnavailable,
            lease.GetDispatchAuthorization());
        Assert.Equal(0, Assert.Single(native.LifetimePins).DisposeCount);
        native.ForegroundWindow = (nint)100;
        Assert.Equal(
            ExactWheelDispatchAuthorization.Authorized,
            lease.GetDispatchAuthorization());
        Assert.Null(lease.Failure);
    }

    [Theory]
    [InlineData(ExactWheelInputEventType.MouseMove)]
    [InlineData(ExactWheelInputEventType.MouseButtonDown)]
    [InlineData(ExactWheelInputEventType.MouseButtonUp)]
    [InlineData(ExactWheelInputEventType.VerticalWheel)]
    [InlineData(ExactWheelInputEventType.HorizontalWheel)]
    public void PlaybackTargetLease_MouseDispatchRequiresExactLeasedRoot(
        ExactWheelInputEventType eventType)
    {
        var native = ReadyNative();
        native.ForegroundWindow = (nint)100;
        native.RootWindowAtPoint = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        var inputEvent = new ExactWheelInputEvent(
            0,
            1,
            eventType,
            -240,
            360,
            eventType is ExactWheelInputEventType.MouseButtonDown or
                ExactWheelInputEventType.MouseButtonUp
                ? (int)ExactWheelMouseButton.Left
                : 0,
            0);

        Assert.Equal(
            ExactWheelDispatchAuthorization.Authorized,
            lease.GetDispatchAuthorization(inputEvent));
        Assert.Equal((-240, 360), native.LastHitTestPoint!.Value);

        native.RootWindowAtPoint = (nint)200;
        Assert.False(lease.TryAuthorizeDispatch(inputEvent, out var failure));
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.PointerTargetMismatch,
            failure?.Kind);
        Assert.Equal(
            ExactWheelDispatchAuthorization.TemporarilyUnavailable,
            lease.GetDispatchAuthorization(inputEvent));
        Assert.Null(lease.Failure);

        native.RootWindowAtPoint = (nint)100;
        Assert.True(lease.IsDispatchAuthorized(inputEvent));
    }

    [Fact]
    public void PlaybackTargetLease_KeyboardDispatchUsesForegroundNotPointer()
    {
        var native = ReadyNative();
        native.ForegroundWindow = (nint)100;
        native.RootWindowAtPoint = (nint)200;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        var inputEvent = new ExactWheelInputEvent(
            0,
            1,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            0x41,
            0x1E);

        Assert.Equal(
            ExactWheelDispatchAuthorization.Authorized,
            lease.GetDispatchAuthorization(inputEvent));
        Assert.Null(native.LastHitTestPoint);
    }

    [Fact]
    public void PlaybackTargetLease_StopsWhenOriginalPinnedProcessExits()
    {
        var native = ReadyNative();
        native.ForegroundWindow = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        var pin = Assert.Single(native.LifetimePins);
        pin.IsExitObservedAlive = false;
        pin.IsRetainedProcessAlive = false;

        Assert.False(lease.TryAuthorizeDispatch(out var failure));
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.ProcessExited,
            failure?.Kind);
        Assert.Equal(1, pin.DisposeCount);
    }

    [Fact]
    public void PlaybackTargetLease_DelayedExitEventCannotAuthorizePidHwndReuse()
    {
        var native = ReadyNative();
        native.ForegroundWindow = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        var pin = Assert.Single(native.LifetimePins);

        // Model a starved Process.Exited callback: the managed event-backed
        // flag is stale, while the exact retained kernel process handle is
        // already signaled. Even if the numeric PID/HWND still match, input
        // must fail closed.
        pin.IsExitObservedAlive = true;
        pin.IsRetainedProcessAlive = false;

        Assert.False(lease.TryAuthorizeDispatch(out var failure));
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.ProcessExited,
            failure?.Kind);
        Assert.Equal(1, pin.DisposeCount);
    }

    [Fact]
    public void PlaybackTargetLease_ThrottlesAndPinsFullIdentityRefresh()
    {
        var native = ReadyNative();
        native.ForegroundWindow = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            Identity,
            (nint)100,
            TimeSpan.FromSeconds(1));
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        var pin = Assert.Single(native.LifetimePins);

        Assert.True(lease.IsDispatchAuthorized());
        native.Advance(TimeSpan.FromMilliseconds(999));
        Assert.True(lease.IsDispatchAuthorized());
        Assert.Equal(0, pin.VerificationCount);

        pin.VerificationStatus =
            RobloxProcessVerificationStatus.StartTimeMismatch;
        native.Advance(TimeSpan.FromMilliseconds(2));

        Assert.False(lease.TryAuthorizeDispatch(out var failure));
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.IdentityRejected,
            failure?.Kind);
        Assert.Equal(1, pin.VerificationCount);
        Assert.Equal([false], pin.ForceTrustRefreshCalls);
        pin.VerificationStatus = RobloxProcessVerificationStatus.Verified;
        Assert.False(lease.IsDispatchAuthorized());
        Assert.Equal(1, pin.VerificationCount);
    }

    [Fact]
    public void PlaybackTargetLease_RevalidatesOnlyTargetsUsedByCurrentEvent()
    {
        var secondIdentity = Identity with
        {
            ProcessId = 43,
            StartTimeUtc = Identity.StartTimeUtc.AddSeconds(1)
        };
        var native = ReadyNative();
        native.AcceptedIdentities = [Identity, secondIdentity];
        native.WindowProcessIds[(nint)100] = Identity.ProcessId;
        native.WindowProcessIds[(nint)101] = secondIdentity.ProcessId;
        native.ForegroundWindow = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            [
                new RobloxPlaybackTarget(Identity, (nint)100),
                new RobloxPlaybackTarget(secondIdentity, (nint)101)
            ],
            TimeSpan.FromSeconds(1));
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        var firstPin = native.LifetimePins[0];
        var secondPin = native.LifetimePins[1];

        native.Advance(TimeSpan.FromSeconds(1));

        Assert.True(lease.IsDispatchAuthorized());
        Assert.Equal(1, firstPin.VerificationCount);
        Assert.Equal(0, secondPin.VerificationCount);

        native.ForegroundWindow = (nint)101;

        Assert.True(lease.IsDispatchAuthorized());
        Assert.Equal(1, firstPin.VerificationCount);
        Assert.Equal(1, secondPin.VerificationCount);
    }

    [Fact]
    public void PlaybackTargetLease_EightClientsRefreshesOnlyActiveIdentity()
    {
        var identities = Enumerable.Range(0, 8)
            .Select(index => Identity with
            {
                ProcessId = Identity.ProcessId + index,
                StartTimeUtc = Identity.StartTimeUtc.AddSeconds(index)
            })
            .ToArray();
        var targets = identities
            .Select((identity, index) => new RobloxPlaybackTarget(
                identity,
                (nint)(100 + index)))
            .ToArray();
        var native = ReadyNative();
        native.AcceptedIdentities = identities;
        foreach (var target in targets)
        {
            native.WindowProcessIds[target.Handle] =
                target.Identity.ProcessId;
        }
        native.ForegroundWindow = targets[0].Handle;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            targets,
            TimeSpan.FromSeconds(1));
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        native.WindowProcessIdReads.Clear();

        native.Advance(TimeSpan.FromSeconds(1));

        Assert.True(lease.IsDispatchAuthorized());
        Assert.Equal(
            1,
            native.LifetimePins.Sum(pin => pin.VerificationCount));
        Assert.Equal(
            [targets[0].Handle],
            native.WindowProcessIdReads);
    }

    [Fact]
    public void PlaybackTargetLease_LivenessSweepDetectsInactiveClientExit()
    {
        var secondIdentity = Identity with
        {
            ProcessId = 43,
            StartTimeUtc = Identity.StartTimeUtc.AddSeconds(1)
        };
        var native = ReadyNative();
        native.AcceptedIdentities = [Identity, secondIdentity];
        native.WindowProcessIds[(nint)100] = Identity.ProcessId;
        native.WindowProcessIds[(nint)101] = secondIdentity.ProcessId;
        native.ForegroundWindow = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            [
                new RobloxPlaybackTarget(Identity, (nint)100),
                new RobloxPlaybackTarget(secondIdentity, (nint)101)
            ],
            TimeSpan.FromSeconds(1));
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        native.LifetimePins[1].IsExitObservedAlive = false;
        native.LifetimePins[1].IsRetainedProcessAlive = false;

        native.Advance(TimeSpan.FromSeconds(1));

        Assert.False(lease.TryAuthorizeDispatch(out var failure));
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.ProcessExited,
            failure?.Kind);
        Assert.All(
            native.LifetimePins,
            pin => Assert.Equal(1, pin.DisposeCount));
        Assert.All(
            native.LifetimePins,
            pin => Assert.Equal(0, pin.VerificationCount));
    }

    [Fact]
    public void PlaybackTargetLease_DoesNotRevalidateSameMouseTargetTwice()
    {
        var native = ReadyNative();
        native.ForegroundWindow = (nint)100;
        native.RootWindowAtPoint = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        native.WindowProcessIdReads.Clear();
        var inputEvent = new ExactWheelInputEvent(
            0,
            1,
            ExactWheelInputEventType.MouseMove,
            20,
            30,
            0,
            0);

        Assert.True(lease.IsDispatchAuthorized(inputEvent));
        Assert.Equal([(nint)100], native.WindowProcessIdReads);
    }

    [Fact]
    public void PlaybackTargetLease_DisposalReleasesPinsAndDeniesDispatch()
    {
        var native = ReadyNative();
        native.ForegroundWindow = (nint)100;
        var result = CreateService(native).AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        var lease = Assert.IsType<RobloxPlaybackTargetLease>(result.Lease);
        var pin = Assert.Single(native.LifetimePins);

        lease.Dispose();
        lease.Dispose();

        Assert.Equal(1, pin.DisposeCount);
        Assert.False(lease.TryAuthorizeDispatch(out var failure));
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.Disposed,
            failure?.Kind);
    }

    [Fact]
    public void Win32Adapter_RejectsReusedCurrentProcessIdentityByStartTime()
    {
        using var process = Process.GetCurrentProcess();
        var stale = new RobloxClientProcessIdentity(
            process.Id,
            process.StartTime.ToUniversalTime().AddSeconds(1),
            Environment.ProcessPath ?? "SessionDock.Tests.exe");

        var status = new Win32RobloxWindowNativeAdapter().VerifyProcess(
            stale,
            forceTrustRefresh: false);

        Assert.Equal(
            RobloxProcessVerificationStatus.StartTimeMismatch,
            status);
    }

    private static RobloxWindowService CreateService(FakeNative native) =>
        new(native, TimeSpan.FromMilliseconds(10));

    private static FakeNative ReadyNative() => new()
    {
        VerificationStatus = RobloxProcessVerificationStatus.Verified,
        Windows = [(nint)100],
        WindowProcessId = Identity.ProcessId,
        OuterBounds = new RobloxPixelRect(10, 20, 816, 640),
        ClientBounds = new RobloxPixelRect(18, 52, 800, 600)
    };

    private sealed class FakeNative : IRobloxWindowNativeAdapter
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

        public RobloxProcessVerificationStatus VerificationStatus { get; set; }
        public IReadOnlyList<nint> Windows { get; set; } = [];
        public IReadOnlyList<nint> TopLevelZOrder { get; set; } = [];
        public Queue<IReadOnlyList<nint>> WindowEnumerationSequence { get; } =
            new();
        public int WindowProcessId { get; set; }
        public Dictionary<nint, int> WindowProcessIds { get; } = [];
        public List<nint> WindowProcessIdReads { get; } = [];
        public IReadOnlyList<RobloxClientProcessIdentity>? AcceptedIdentities
        { get; set; }
        public RobloxPixelRect OuterBounds { get; set; }
        public RobloxPixelRect ClientBounds { get; set; }
        public Dictionary<nint, RobloxPixelRect> OuterBoundsByWindow { get; } = [];
        public Dictionary<nint, RobloxPixelRect> ClientBoundsByWindow { get; } = [];
        public Queue<(RobloxPixelRect Outer, RobloxPixelRect Client)>
            GeometrySequence
        { get; } = new();
        public RobloxPixelRect RealizedBoundsAfterSet { get; set; }
        public RobloxPixelRect RealizedClientAfterSet { get; set; }
        public bool Usable { get; set; } = true;
        public int UsableReadCount { get; private set; }
        public bool Minimized { get; set; }
        public bool Maximized { get; set; }
        public bool Fullscreen { get; set; }
        public bool RestoreResult { get; set; } = true;
        public bool SetBoundsResult { get; set; } = true;
        public bool SetForegroundResult { get; set; }
        public bool SetForegroundChangesForeground { get; set; }
        public nint ForegroundWindow { get; set; }
        public nint RootWindowAtPoint { get; set; }
        public (int X, int Y)? LastHitTestPoint { get; private set; }
        public int EnumerationCount { get; private set; }
        public bool RestoreCalled { get; private set; }
        public bool SetBoundsCalled { get; private set; }
        public int SetBoundsCallCount { get; private set; }
        public bool SetForegroundCalled { get; private set; }
        public RobloxPixelRect LastRequestedBounds { get; private set; }
        public List<bool> ForceTrustRefreshCalls { get; } = [];
        public List<bool> PinForceTrustRefreshCalls { get; } = [];
        public List<FakeProcessLifetimePin> LifetimePins { get; } = [];
        public int PinAttemptCount { get; private set; }
        public HashSet<nint> TopmostWindows { get; } = [];
        public List<nint> DemotedWindows { get; } = [];
        public List<RobloxWindowZOrderPlacement> AppliedZOrderPlacements
        { get; } = [];

        public RobloxProcessVerificationStatus VerifyProcess(
            RobloxClientProcessIdentity identity,
            bool forceTrustRefresh)
        {
            Assert.Contains(identity, AcceptedIdentities ?? [Identity]);
            ForceTrustRefreshCalls.Add(forceTrustRefresh);
            return VerificationStatus;
        }

        public RobloxProcessVerificationStatus TryPinProcessLifetime(
            RobloxClientProcessIdentity identity,
            bool forceTrustRefresh,
            out IRobloxProcessLifetimePin? lifetimePin)
        {
            Assert.Contains(identity, AcceptedIdentities ?? [Identity]);
            PinAttemptCount++;
            PinForceTrustRefreshCalls.Add(forceTrustRefresh);
            if (VerificationStatus != RobloxProcessVerificationStatus.Verified)
            {
                lifetimePin = null;
                return VerificationStatus;
            }

            var pin = new FakeProcessLifetimePin(identity);
            LifetimePins.Add(pin);
            lifetimePin = pin;
            return RobloxProcessVerificationStatus.Verified;
        }

        public IReadOnlyList<nint> EnumerateTopLevelWindows(int processId)
        {
            Assert.Equal(Identity.ProcessId, processId);
            EnumerationCount++;
            if (WindowEnumerationSequence.Count > 0)
                Windows = WindowEnumerationSequence.Dequeue();
            return Windows;
        }

        public IReadOnlyList<nint> EnumerateTopLevelWindowsInZOrder() =>
            TopLevelZOrder.Count == 0 ? Windows : TopLevelZOrder;

        public bool IsUsableTopLevelWindow(nint windowHandle)
        {
            UsableReadCount++;
            return Usable;
        }

        public int GetWindowProcessId(nint windowHandle)
        {
            WindowProcessIdReads.Add(windowHandle);
            return WindowProcessIds.TryGetValue(windowHandle, out var processId)
                ? processId
                : WindowProcessId;
        }

        public bool IsMinimized(nint windowHandle) => Minimized;

        public bool IsMaximized(nint windowHandle) => Maximized;

        public bool IsFullscreen(nint windowHandle) => Fullscreen;

        public bool TryRestore(nint windowHandle)
        {
            RestoreCalled = true;
            if (RestoreResult)
            {
                Minimized = false;
                Maximized = false;
            }
            return RestoreResult;
        }

        public bool TryGetGeometry(
            nint windowHandle,
            out RobloxPixelRect outerBounds,
            out RobloxPixelRect clientBounds)
        {
            if (windowHandle == (nint)100 && GeometrySequence.Count > 0)
            {
                var next = GeometrySequence.Dequeue();
                OuterBounds = next.Outer;
                ClientBounds = next.Client;
            }

            outerBounds = OuterBoundsByWindow.TryGetValue(
                windowHandle,
                out var perWindowOuter)
                ? perWindowOuter
                : OuterBounds;
            clientBounds = ClientBoundsByWindow.TryGetValue(
                windowHandle,
                out var perWindowClient)
                ? perWindowClient
                : ClientBounds;
            return outerBounds.IsValid && clientBounds.IsValid;
        }

        public bool TrySetBounds(
            nint windowHandle,
            RobloxPixelRect outerBounds)
        {
            SetBoundsCalled = true;
            SetBoundsCallCount++;
            LastRequestedBounds = outerBounds;
            if (SetBoundsResult)
            {
                OuterBounds = RealizedBoundsAfterSet.IsValid
                    ? RealizedBoundsAfterSet
                    : outerBounds;
                ClientBounds = RealizedClientAfterSet.IsValid
                    ? RealizedClientAfterSet
                    : new RobloxPixelRect(
                        outerBounds.Left + 8,
                        outerBounds.Top + 32,
                        Math.Max(1, outerBounds.Width - 16),
                        Math.Max(1, outerBounds.Height - 40));
            }
            return SetBoundsResult;
        }

        public bool IsTopmost(nint windowHandle) =>
            TopmostWindows.Contains(windowHandle);

        public bool TryDemoteTopmostWithoutActivation(nint windowHandle)
        {
            DemotedWindows.Add(windowHandle);
            TopmostWindows.Remove(windowHandle);
            return true;
        }

        public bool TryApplyZOrderWithoutActivation(
            IReadOnlyList<RobloxWindowZOrderPlacement> placements)
        {
            AppliedZOrderPlacements.AddRange(placements);
            return true;
        }

        public bool TrySetForeground(nint windowHandle)
        {
            SetForegroundCalled = true;
            if (SetForegroundResult && SetForegroundChangesForeground)
                ForegroundWindow = windowHandle;
            return SetForegroundResult;
        }

        public nint GetForegroundWindow() => ForegroundWindow;

        public nint GetRootWindowAtPoint(int x, int y)
        {
            LastHitTestPoint = (x, y);
            return RootWindowAtPoint;
        }

        public IReadOnlyList<RobloxMonitor> GetMonitors() => [];

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            return Task.CompletedTask;
        }

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FakeProcessLifetimePin(
        RobloxClientProcessIdentity identity) : IRobloxProcessLifetimePin
    {
        private bool _isRetainedProcessAlive = true;

        public RobloxClientProcessIdentity Identity { get; } = identity;

        public bool IsExitObservedAlive { get; set; } = true;

        public bool IsRetainedProcessAlive
        {
            get
            {
                RetainedLivenessCheckCount++;
                return _isRetainedProcessAlive;
            }
            set => _isRetainedProcessAlive = value;
        }

        public RobloxProcessVerificationStatus VerificationStatus { get; set; } =
            RobloxProcessVerificationStatus.Verified;

        public int VerificationCount { get; private set; }

        public int RetainedLivenessCheckCount { get; private set; }

        public int DisposeCount { get; private set; }

        public List<bool> ForceTrustRefreshCalls { get; } = [];

        public RobloxProcessVerificationStatus RevalidateIdentityAndToken(
            bool refreshExecutableTrust)
        {
            VerificationCount++;
            ForceTrustRefreshCalls.Add(refreshExecutableTrust);
            return IsRetainedProcessAlive
                ? VerificationStatus
                : RobloxProcessVerificationStatus.Exited;
        }

        public void Dispose()
        {
            if (DisposeCount > 0)
                return;
            DisposeCount++;
            IsExitObservedAlive = false;
            IsRetainedProcessAlive = false;
        }
    }

    private static string RepoFile(params string[] components)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null &&
               !File.Exists(Path.Combine(current.FullName, "SessionDock.slnx")))
        {
            current = current.Parent;
        }
        if (current is null)
            throw new DirectoryNotFoundException("Repository root not found.");
        return Path.Combine([current.FullName, .. components]);
    }
}
