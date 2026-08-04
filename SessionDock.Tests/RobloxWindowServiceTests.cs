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

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public async Task WaitForWindowAsync_SharedOperationSerializesForcedTrust(
        int targetCount)
    {
        var identities = Enumerable.Range(0, targetCount)
            .Select(index => Identity with
            {
                ProcessId = Identity.ProcessId + index,
                StartTimeUtc = Identity.StartTimeUtc.AddSeconds(index)
            })
            .ToArray();
        using var forcedPinEntered = new ManualResetEventSlim();
        using var releaseForcedPin = new ManualResetEventSlim();
        var native = ReadyNative();
        native.AcceptedIdentities = identities;
        native.VerificationStatus =
            RobloxProcessVerificationStatus.WrongUserOrSession;
        native.ForcedPinEntered = forcedPinEntered;
        native.ReleaseForcedPin = releaseForcedPin;
        var service = CreateService(native);
        using var trustContext = CreateShareableTrustContext();
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = Task.Run(
            () => service.WaitForWindowAsync(
                identities[0],
                TimeSpan.FromMilliseconds(20),
                trustContext,
                cancellationToken),
            cancellationToken);

        var operations = new List<Task<RobloxWindowOperationResult>> { first };
        try
        {
            var entered = await Task.Run(
                () => forcedPinEntered.Wait(
                    TimeSpan.FromSeconds(5),
                    cancellationToken),
                cancellationToken);
            Assert.True(
                entered,
                "The forced trust verification did not start.");
            for (var index = 1; index < identities.Length; index++)
            {
                operations.Add(service.WaitForWindowAsync(
                    identities[index],
                    TimeSpan.FromMilliseconds(20),
                    trustContext,
                    cancellationToken));
            }

            Assert.Equal(1, native.PinAttemptCount);
        }
        finally
        {
            releaseForcedPin.Set();
        }

        var results = await Task.WhenAll(operations);
        Assert.All(
            results,
            result => Assert.Equal(
                RobloxWindowOperationStatus.IdentityRejected,
                result.Status));
        Assert.Equal(targetCount, native.PinAttemptCount);
        Assert.Equal(
            1,
            native.PinForceTrustRefreshCalls.Count(forceRefresh =>
                forceRefresh));
        Assert.Equal(
            targetCount - 1,
            native.PinForceTrustRefreshCalls.Count(forceRefresh =>
                !forceRefresh));
        Assert.Equal(
            1,
            native.PinVerifyExecutableTrustCalls.Count(verify => verify));
        Assert.Equal(
            targetCount - 1,
            native.PinVerifyExecutableTrustCalls.Count(verify => !verify));
        Assert.All(native.PinExecutableTrustHandles, Assert.NotNull);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public async Task CaptureAsync_SharedOperationSerializesForcedTrust(
        int targetCount)
    {
        var identities = Enumerable.Range(0, targetCount)
            .Select(index => Identity with
            {
                ProcessId = Identity.ProcessId + index,
                StartTimeUtc = Identity.StartTimeUtc.AddSeconds(index)
            })
            .ToArray();
        var handles = Enumerable.Range(0, targetCount)
            .Select(index => (nint)(100 + index))
            .ToArray();
        using var forcedVerificationEntered = new ManualResetEventSlim();
        using var releaseForcedVerification = new ManualResetEventSlim();
        var native = ReadyNative();
        native.AcceptedIdentities = identities;
        native.ForcedVerificationEntered = forcedVerificationEntered;
        native.ReleaseForcedVerification = releaseForcedVerification;
        for (var index = 0; index < targetCount; index++)
        {
            native.WindowProcessIds[handles[index]] =
                identities[index].ProcessId;
            native.OuterBoundsByWindow[handles[index]] =
                native.OuterBounds;
            native.ClientBoundsByWindow[handles[index]] =
                native.ClientBounds;
        }
        var service = CreateService(native);
        using var trustContext = CreateShareableTrustContext();
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = Task.Run(
            () => service.CaptureAsync(
                identities[0],
                handles[0],
                trustContext,
                cancellationToken),
            cancellationToken);
        var operations = new List<Task<RobloxWindowOperationResult>> { first };
        try
        {
            var entered = await Task.Run(
                () => forcedVerificationEntered.Wait(
                    TimeSpan.FromSeconds(5),
                    cancellationToken),
                cancellationToken);
            Assert.True(
                entered,
                "The forced trust verification did not start.");
            for (var index = 1; index < identities.Length; index++)
            {
                operations.Add(service.CaptureAsync(
                    identities[index],
                    handles[index],
                    trustContext,
                    cancellationToken));
            }

            Assert.Single(native.ForceTrustRefreshCalls);
        }
        finally
        {
            releaseForcedVerification.Set();
        }

        var results = await Task.WhenAll(operations);
        Assert.All(results, result => Assert.True(result.Success, result.Error));
        Assert.Equal(
            1,
            native.ForceTrustRefreshCalls.Count(forceRefresh =>
                forceRefresh));
        Assert.Equal(
            targetCount - 1,
            native.ForceTrustRefreshCalls.Count(forceRefresh =>
                !forceRefresh));
        Assert.Equal(
            1,
            native.VerifyExecutableTrustCalls.Count(verify => verify));
        Assert.Equal(
            targetCount - 1,
            native.VerifyExecutableTrustCalls.Count(verify => !verify));
        Assert.All(native.ExecutableTrustHandles, Assert.NotNull);
    }

    [Fact]
    public async Task SharedTrust_StaleFirstProcessDoesNotConsumeForcedProof()
    {
        var native = ReadyNative();
        var service = CreateService(native);
        using var context = new RobloxExecutableTrustContext();
        native.VerificationStatus =
            RobloxProcessVerificationStatus.StartTimeMismatch;

        var stale = await service.WaitForWindowAsync(
            Identity,
            TimeSpan.FromMilliseconds(20),
            context,
            TestContext.Current.CancellationToken);
        native.VerificationStatus = RobloxProcessVerificationStatus.Verified;
        var valid = await service.WaitForWindowAsync(
            Identity,
            TimeSpan.FromMilliseconds(20),
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(RobloxWindowOperationStatus.IdentityRejected, stale.Status);
        Assert.True(valid.Success, valid.Error);
        Assert.Equal([true, true], native.PinForceTrustRefreshCalls);
        Assert.Equal([true, true], native.PinVerifyExecutableTrustCalls);
    }

    [Fact]
    public async Task SharedTrust_RejectionFailsFollowersWithoutNativeRetry()
    {
        var native = ReadyNative();
        native.VerificationStatus =
            RobloxProcessVerificationStatus.ExecutableNotTrusted;
        var service = CreateService(native);
        using var context = CreateShareableTrustContext();

        var first = await service.CaptureAsync(
            Identity,
            (nint)100,
            context,
            TestContext.Current.CancellationToken);
        var second = await service.CaptureAsync(
            Identity,
            (nint)100,
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(RobloxWindowOperationStatus.IdentityRejected, first.Status);
        Assert.Equal(RobloxWindowOperationStatus.IdentityRejected, second.Status);
        Assert.Single(native.ForceTrustRefreshCalls);
        Assert.Single(native.VerifyExecutableTrustCalls);
    }

    [Fact]
    public async Task SharedTrust_WithoutAFileLeaseNeverSkipsNativeTrust()
    {
        var native = ReadyNative();
        var service = CreateService(native);
        using var context = new RobloxExecutableTrustContext();

        var first = await service.CaptureAsync(
            Identity,
            (nint)100,
            context,
            TestContext.Current.CancellationToken);
        var second = await service.CaptureAsync(
            Identity,
            (nint)100,
            context,
            TestContext.Current.CancellationToken);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal([true, true], native.ForceTrustRefreshCalls);
        Assert.Equal([true, true], native.VerifyExecutableTrustCalls);
    }

    [Fact]
    public void SharedTrust_RetainsVerifiedPathUntilContextIsDisposed()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"SessionDock.TrustContext.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "RobloxPlayerBeta.exe");
        var replacement = Path.Combine(root, "replacement.exe");
        File.WriteAllBytes(executable, [1, 2, 3, 4]);
        File.WriteAllBytes(replacement, [5, 6, 7, 8]);
        try
        {
            using (var context = new RobloxExecutableTrustContext())
            {
                using (var claim = context.AcquireVerification(
                           executable,
                           TestContext.Current.CancellationToken))
                {
                    Assert.True(claim.VerifyExecutableTrust);
                    claim.ReportVerification(
                        RobloxProcessVerificationStatus.Verified);
                }
                using (var follower = context.AcquireVerification(
                           executable,
                           TestContext.Current.CancellationToken))
                {
                    Assert.False(follower.VerifyExecutableTrust);
                }

                var replacementError = Record.Exception(() =>
                    File.Move(replacement, executable, overwrite: true));
                Assert.True(
                    replacementError is IOException or
                        UnauthorizedAccessException,
                    $"Expected the retained executable handle to block replacement, but got {replacementError?.GetType().Name ?? "no exception"}.");
            }

            File.Move(replacement, executable, overwrite: true);
            Assert.Equal([5, 6, 7, 8], File.ReadAllBytes(executable));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
    public async Task FocusAsync_WithRetainedLease_InterventionGuardBlocksActivation()
    {
        var native = ReadyNative();
        native.Minimized = true;
        native.SetForegroundResult = true;
        native.SetForegroundChangesForeground = true;
        var service = CreateService(native);
        var acquisition = service.AcquirePlaybackTargetLease(
            Identity,
            (nint)100);
        using var lease = Assert.IsType<RobloxPlaybackTargetLease>(
            acquisition.Lease);
        var activationAllowed = false;

        var blocked = await service.FocusAsync(
            lease,
            Identity,
            (nint)100,
            TimeSpan.FromMilliseconds(20),
            () => activationAllowed,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            RobloxWindowOperationStatus.FocusDenied,
            blocked.Status);
        Assert.False(native.RestoreCalled);
        Assert.False(native.SetForegroundCalled);

        activationAllowed = true;
        var focused = await service.FocusAsync(
            lease,
            Identity,
            (nint)100,
            TimeSpan.FromMilliseconds(20),
            () => activationAllowed,
            TestContext.Current.CancellationToken);

        Assert.True(focused.Success, focused.Error);
        Assert.True(native.RestoreCalled);
        Assert.True(native.SetForegroundCalled);
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

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public void SessionMacroLeaseCache_RetryReacquiresAfterStickyFailure(
        int targetCount)
    {
        var identities = Enumerable.Range(0, targetCount)
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
        var native = ReadyNative();
        native.AcceptedIdentities = identities;
        foreach (var window in windows)
            native.WindowProcessIds[window.Handle] = window.Identity.ProcessId;
        var service = CreateService(native);
        var capturedWindowClasses = 0;
        using var cache = new SessionMacroPlaybackLeaseCache(_ =>
        {
            capturedWindowClasses++;
            return "WINDOWSCLIENT";
        });
        long timestamp = 0;
        var retries = new SessionMacroPlaybackRetryTracker(
            () => timestamp,
            timestampFrequency: 1_000);

        var first = cache.GetOrAcquire(service, windows);
        Assert.True(first.Success, first.Failure?.Error);
        _ = cache.GetOrCaptureWindowClass(windows[0]);
        native.Usable = false;
        var stickyFailure = first.Lease!.ValidateExactTarget(
            windows[0].Identity,
            windows[0].Handle,
            revalidateIdentityAndToken: false);
        Assert.Equal(
            RobloxPlaybackTargetLeaseFailureKind.WindowUnavailable,
            stickyFailure?.Kind);
        retries.ReportFailure(
            "retry-target",
            SessionMacroPlaybackRetryDisposition.Transient);
        Assert.False(retries.CanAttempt("retry-target"));

        native.Usable = true;
        timestamp = 250;
        Assert.True(retries.CanAttempt("retry-target"));
        var reacquired = cache.GetOrAcquire(service, windows);

        Assert.True(reacquired.Success, reacquired.Failure?.Error);
        Assert.NotSame(first.Lease, reacquired.Lease);
        Assert.Equal(targetCount * 2, native.PinAttemptCount);
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, cache.CachedWindowClassCount);
        Assert.Equal("WINDOWSCLIENT", cache.GetOrCaptureWindowClass(windows[0]));
        Assert.Equal(2, capturedWindowClasses);
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
        using var cache = new SessionMacroPlaybackLeaseCache(
            _ => "WINDOWSCLIENT",
            CreateShareableTrustContext());
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

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public void SessionMacroLeaseCache_ForceRefreshesSharedExecutableOnce(
        int targetCount)
    {
        var identities = Enumerable.Range(0, targetCount)
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
        var native = ReadyNative();
        native.AcceptedIdentities = identities;
        foreach (var window in windows)
            native.WindowProcessIds[window.Handle] = window.Identity.ProcessId;
        using var cache = new SessionMacroPlaybackLeaseCache(
            _ => "WINDOWSCLIENT",
            CreateShareableTrustContext());

        var acquired = cache.GetOrAcquire(CreateService(native), windows);

        Assert.True(acquired.Success, acquired.Failure?.Error);
        Assert.Equal(targetCount, native.PinAttemptCount);
        Assert.Equal(
            1,
            native.PinForceTrustRefreshCalls.Count(forceRefresh =>
                forceRefresh));
        Assert.Equal(
            targetCount - 1,
            native.PinForceTrustRefreshCalls.Count(forceRefresh =>
                !forceRefresh));
        Assert.Equal(
            1,
            native.PinVerifyExecutableTrustCalls.Count(verify => verify));
        Assert.Equal(
            targetCount - 1,
            native.PinVerifyExecutableTrustCalls.Count(verify => !verify));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public void SessionMacroLeaseCache_SameImmutableTargetListHitIsConstantTime(
        int targetCount)
    {
        var identities = Enumerable.Range(0, targetCount)
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
        var countedWindows = new CountingReadOnlyList<
            RobloxSessionLayoutWindow>(windows);
        var native = ReadyNative();
        native.AcceptedIdentities = identities;
        foreach (var window in windows)
            native.WindowProcessIds[window.Handle] = window.Identity.ProcessId;
        var service = CreateService(native);
        using var cache = new SessionMacroPlaybackLeaseCache();
        var first = cache.GetOrAcquire(service, countedWindows);
        Assert.True(first.Success, first.Failure?.Error);
        countedWindows.ResetIndexReadCount();

        for (var index = 0; index < 1_000; index++)
        {
            var cached = cache.GetOrAcquire(service, countedWindows);
            Assert.Same(first.Lease, cached.Lease);
        }

        Assert.Equal(0, countedWindows.IndexReadCount);
        Assert.Equal(targetCount, native.PinAttemptCount);
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
            forceTrustRefresh: false,
            verifyExecutableTrust: true,
            executableTrustHandle: null);

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
        private readonly object _pinSync = new();
        private readonly object _verificationSync = new();
        private int _pinAttemptCount;

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
        public List<bool> VerifyExecutableTrustCalls { get; } = [];
        public List<Microsoft.Win32.SafeHandles.SafeFileHandle?>
            ExecutableTrustHandles
        { get; } = [];
        public List<bool> PinForceTrustRefreshCalls { get; } = [];
        public List<bool> PinVerifyExecutableTrustCalls { get; } = [];
        public List<Microsoft.Win32.SafeHandles.SafeFileHandle?>
            PinExecutableTrustHandles
        { get; } = [];
        public List<FakeProcessLifetimePin> LifetimePins { get; } = [];
        public int PinAttemptCount => Volatile.Read(ref _pinAttemptCount);
        public ManualResetEventSlim? ForcedPinEntered { get; set; }
        public ManualResetEventSlim? ReleaseForcedPin { get; set; }
        public ManualResetEventSlim? ForcedVerificationEntered { get; set; }
        public ManualResetEventSlim? ReleaseForcedVerification { get; set; }
        public HashSet<nint> TopmostWindows { get; } = [];
        public List<nint> DemotedWindows { get; } = [];
        public List<RobloxWindowZOrderPlacement> AppliedZOrderPlacements
        { get; } = [];

        public RobloxProcessVerificationStatus VerifyProcess(
            RobloxClientProcessIdentity identity,
            bool forceTrustRefresh,
            bool verifyExecutableTrust,
            Microsoft.Win32.SafeHandles.SafeFileHandle?
                executableTrustHandle)
        {
            Assert.Contains(identity, AcceptedIdentities ?? [Identity]);
            lock (_verificationSync)
            {
                ForceTrustRefreshCalls.Add(forceTrustRefresh);
                VerifyExecutableTrustCalls.Add(verifyExecutableTrust);
                ExecutableTrustHandles.Add(executableTrustHandle);
            }
            if (forceTrustRefresh &&
                ForcedVerificationEntered is { } entered)
            {
                entered.Set();
                _ = ReleaseForcedVerification?.Wait(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
            }
            return VerificationStatus;
        }

        public RobloxProcessVerificationStatus TryPinProcessLifetime(
            RobloxClientProcessIdentity identity,
            bool forceTrustRefresh,
            bool verifyExecutableTrust,
            Microsoft.Win32.SafeHandles.SafeFileHandle?
                executableTrustHandle,
            out IRobloxProcessLifetimePin? lifetimePin)
        {
            Assert.Contains(identity, AcceptedIdentities ?? [Identity]);
            Interlocked.Increment(ref _pinAttemptCount);
            lock (_pinSync)
            {
                PinForceTrustRefreshCalls.Add(forceTrustRefresh);
                PinVerifyExecutableTrustCalls.Add(verifyExecutableTrust);
                PinExecutableTrustHandles.Add(executableTrustHandle);
            }
            if (forceTrustRefresh && ForcedPinEntered is { } entered)
            {
                entered.Set();
                _ = ReleaseForcedPin?.Wait(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
            }
            if (VerificationStatus != RobloxProcessVerificationStatus.Verified)
            {
                lifetimePin = null;
                return VerificationStatus;
            }

            var pin = new FakeProcessLifetimePin(identity);
            lock (_pinSync)
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

    private sealed class CountingReadOnlyList<T>(IReadOnlyList<T> values) :
        IReadOnlyList<T>
    {
        internal int IndexReadCount { get; private set; }

        public int Count => values.Count;

        public T this[int index]
        {
            get
            {
                IndexReadCount++;
                return values[index];
            }
        }

        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        internal void ResetIndexReadCount() => IndexReadCount = 0;
    }

    private static RobloxExecutableTrustContext
        CreateShareableTrustContext() =>
        new(_ => File.OpenHandle(
            typeof(RobloxWindowServiceTests).Assembly.Location,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess));

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
