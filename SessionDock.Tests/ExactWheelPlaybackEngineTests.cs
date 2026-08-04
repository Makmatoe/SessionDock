using SessionDock.ExactWheel;
using SessionDock.ExactWheel.Windows;

namespace SessionDock.Tests;

public sealed class ExactWheelPlaybackEngineTests
{
    [Fact]
    public async Task PlayAsync_EmptyRecording_IsRejectedBeforeWorkerStarts()
    {
        var clock = new FakeClock(1_000_000, 1_000);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events: [],
            durationMicroseconds: 0);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            engine.PlayAsync(
                recording,
                new ExactWheelPlaybackOptions
                {
                    Infinite = true,
                    StopOnPhysicalInput = false
                },
                CancellationToken.None));

        Assert.Contains(
            "at least one input event",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(waiter.Deadlines);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_InfiniteZeroDurationRecording_UsesSafeLoopFloor()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeClock(1_000_000, 1_000);
        var waiter = new FakeWaiter(
            clock,
            (_, call, _, _) =>
            {
                if (call == 2)
                    cancellation.Cancel();
                return DeadlineWaitResult.Reached;
            });
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events: [ExactWheelTestData.Events()[0]],
            durationMicroseconds: 0);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                Infinite = true,
                StopOnPhysicalInput = false
            },
            cancellation.Token);

        Assert.Equal(ExactWheelPlaybackStopReason.Cancelled, result.Reason);
        Assert.Equal([1_000L, 1_000L, 11_000L], waiter.Deadlines);
        Assert.Single(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_TwoLoops_UsesFixedOriginAndGuardsEveryDispatch()
    {
        var clock = new FakeClock(1_000_000, 1_000);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var guardCalls = 0;
        var engine = CreateEngine(clock, waiter, backend);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                LoopCount = 2,
                InterLoopDelayMicroseconds = 50_000,
                StopOnPhysicalInput = false,
                PreDispatchGuard = () =>
                {
                    guardCalls++;
                    return true;
                }
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(28, guardCalls);
        Assert.Equal(14, backend.Batches.Count);
        Assert.Equal(
            [
                1_000L,
                11_000L,
                21_000L,
                31_000L,
                41_000L,
                51_000L,
                61_000L,
                501_000L,
                551_000L,
                561_000L,
                571_000L,
                581_000L,
                591_000L,
                601_000L,
                611_000L,
                1_051_000L
            ],
            waiter.Deadlines);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_GuardFailure_StopsBeforeInjection()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                PreDispatchGuard = static () => false
            },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.TargetLost, result.Reason);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_LegacyGuardIsRecheckedAdjacentToInjection()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var guardCalls = 0;
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                new ExactWheelInputEvent(
                    0,
                    1,
                    ExactWheelInputEventType.MouseMove,
                    100,
                    80,
                    0,
                    0)
            ],
            durationMicroseconds: 0);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                PreDispatchGuard = () => ++guardCalls == 1
            },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.TargetLost, result.Reason);
        Assert.Equal(2, guardCalls);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_EventGuardReceivesEventAtFinalRecheck()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var guardedEvents = new List<ExactWheelInputEvent>();
        var engine = CreateEngine(clock, waiter, backend);
        var expectedEvent = new ExactWheelInputEvent(
            0,
            7,
            ExactWheelInputEventType.VerticalWheel,
            -200,
            300,
            120,
            0);
        var recording = ExactWheelTestData.Recording(
            events: [expectedEvent],
            durationMicroseconds: 0);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                EventDispatchAuthorization = inputEvent =>
                {
                    guardedEvents.Add(inputEvent);
                    return ExactWheelDispatchAuthorization.Authorized;
                }
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([expectedEvent, expectedEvent], guardedEvents);
        Assert.Single(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_ConflictingSafetyPoliciesAreRejected()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var engine = CreateEngine(clock, waiter, new FakeInputBackend());

        await Assert.ThrowsAsync<ArgumentException>(() => engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = true,
                PauseOnPhysicalInput = true
            },
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                DispatchAuthorization = static () =>
                    ExactWheelDispatchAuthorization.Authorized,
                EventDispatchAuthorization = static _ =>
                    ExactWheelDispatchAuthorization.Authorized
            },
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                PreDispatchGuard = static () => true,
                DispatchAuthorization = static () =>
                    ExactWheelDispatchAuthorization.Authorized
            },
            CancellationToken.None));

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_HighRateTransientTargetGap_PausesThenContinues()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var guardCalls = 0;
        var engine = CreateEngine(clock, waiter, backend);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                Rate = ExactWheelPlaybackRate.FromRatio(2, 1),
                DispatchAuthorization = () => ++guardCalls >= 3
                    ? ExactWheelDispatchAuthorization.Authorized
                    : ExactWheelDispatchAuthorization.TemporarilyUnavailable
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(16, guardCalls);
        Assert.Equal(7, backend.Batches.Count);
        Assert.Equal(
            [10L, 10_010L, 20_010L],
            waiter.Deadlines.Take(3));
        Assert.Equal(270_010L, waiter.Deadlines[^1]);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_TemporaryTargetWaitsUntilTerminalDenial()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var guardCalls = 0;
        var engine = CreateEngine(clock, waiter, backend);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                DispatchAuthorization = () => ++guardCalls >= 4
                    ? ExactWheelDispatchAuthorization.Denied
                    : ExactWheelDispatchAuthorization.TemporarilyUnavailable
            },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.TargetLost, result.Reason);
        Assert.Equal(4, guardCalls);
        Assert.Empty(backend.Batches);
        Assert.Equal(
            [10L, 10_010L, 20_010L, 30_010L],
            waiter.Deadlines);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_TemporaryTargetWaitIsCancellationAware()
    {
        var clock = new FakeClock(1_000_000, 10);
        using var cancellation = new CancellationTokenSource();
        var waiter = new FakeWaiter(
            clock,
            (_, call, _, _) =>
            {
                if (call == 1)
                    cancellation.Cancel();
                return DeadlineWaitResult.Reached;
            });
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                DispatchAuthorization = static () =>
                    ExactWheelDispatchAuthorization.TemporarilyUnavailable
            },
            cancellation.Token);

        Assert.Equal(ExactWheelPlaybackStopReason.Cancelled, result.Reason);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_PhysicalInputDuringTargetGap_StopsBeforeInjection()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(
            clock,
            (_, call, _, intervention) =>
            {
                if (call == 0)
                    return DeadlineWaitResult.Reached;
                Assert.NotNull(intervention);
                return DeadlineWaitResult.PhysicalIntervention;
            });
        var backend = new FakeInputBackend();
        var capture = new FakeCapture();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            waiter,
            new FakePhysicalInputState(),
            () => capture);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                DispatchAuthorization = static () =>
                    ExactWheelDispatchAuthorization.TemporarilyUnavailable
            },
            CancellationToken.None);

        Assert.Equal(
            ExactWheelPlaybackStopReason.PhysicalIntervention,
            result.Reason);
        Assert.True(capture.InterventionStarted);
        Assert.True(capture.InterventionStopped);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_PausePolicy_AllowsPhysicalRefocusAndShiftsTimeline()
    {
        var clock = new FakeClock(1_000_000, 10);
        var physicalInput = new FakePhysicalInputState();
        var authorizationCalls = 0;
        var waiter = new FakeWaiter(
            clock,
            (_, call, _, intervention) =>
            {
                if (call == 0)
                {
                    physicalInput.AllReleased = false;
                    Assert.IsType<EventWaitHandle>(intervention).Set();
                    return DeadlineWaitResult.PhysicalIntervention;
                }

                physicalInput.AllReleased = true;
                if (call == 1)
                    clock.Timestamp += 300_000;
                return DeadlineWaitResult.Reached;
            });
        var backend = new FakeInputBackend();
        var capture = new FakeCapture();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            waiter,
            physicalInput,
            () => capture);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                PauseOnPhysicalInput = true,
                DispatchAuthorization = () => ++authorizationCalls >= 2
                    ? ExactWheelDispatchAuthorization.Authorized
                    : ExactWheelDispatchAuthorization.TemporarilyUnavailable
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(15, authorizationCalls);
        Assert.Equal(7, backend.Batches.Count);
        Assert.Equal(810_010L, waiter.Deadlines[^1]);
        Assert.True(capture.InterventionStarted);
        Assert.True(capture.InterventionStopped);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_ResetBeforeReleaseDoesNotEraseRacingIntervention()
    {
        var clock = new FakeClock(1_000_000, 10);
        EventWaitHandle? interventionSignal = null;
        var physicalInput = new FakePhysicalInputState
        {
            ReleaseProbe = call =>
            {
                if (call == 3)
                    interventionSignal!.Set();
                return true;
            }
        };
        var waiter = new FakeWaiter(
            clock,
            (_, call, _, intervention) =>
            {
                if (call == 0)
                {
                    interventionSignal =
                        Assert.IsType<EventWaitHandle>(intervention);
                    interventionSignal.Set();
                    return DeadlineWaitResult.PhysicalIntervention;
                }
                return DeadlineWaitResult.Reached;
            });
        var backend = new FakeInputBackend();
        var authorizationCalls = 0;
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            waiter,
            physicalInput,
            static () => new FakeCapture());

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                PauseOnPhysicalInput = true,
                DispatchAuthorization = () =>
                {
                    authorizationCalls++;
                    return ExactWheelDispatchAuthorization.Authorized;
                }
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(15, authorizationCalls);
        Assert.Equal(7, backend.Batches.Count);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_PartialSendInput_StopsWithoutRetry()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend((inputs, _) =>
            (checked((uint)inputs.Length - 1U), 5));
        var engine = CreateEngine(clock, waiter, backend);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions { StopOnPhysicalInput = false },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.InjectionFailed, result.Reason);
        Assert.Equal(5, result.Win32Error);
        Assert.Single(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_DangerousLateness_AbortsBeforeInjection()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(
            clock,
            (deadline, _, _, _) =>
            {
                clock.Timestamp = deadline + 250_001;
                return DeadlineWaitResult.Reached;
            });
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                DangerouslyLateMicroseconds = 250_000
            },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.DangerouslyLate, result.Reason);
        Assert.Equal(250_001, result.LatenessMicroseconds);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_PhysicalInputHeld_StopsBeforeMonitorOrInjection()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var capture = new FakeCapture();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            waiter,
            new FakePhysicalInputState { AllReleased = false },
            () => capture);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions(),
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.PhysicalInputHeld, result.Reason);
        Assert.False(capture.InterventionStarted);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_InterventionSignal_StopsAndTearsDownMonitor()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(
            clock,
            (_, _, _, intervention) =>
            {
                Assert.NotNull(intervention);
                return DeadlineWaitResult.PhysicalIntervention;
            });
        var backend = new FakeInputBackend();
        var capture = new FakeCapture();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            waiter,
            new FakePhysicalInputState(),
            () => capture);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions(),
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.PhysicalIntervention, result.Reason);
        Assert.True(capture.InterventionStarted);
        Assert.True(capture.InterventionStopped);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_CancellationToken_StopsBeforeInput()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions { StopOnPhysicalInput = false },
            cancellation.Token);

        Assert.Equal(ExactWheelPlaybackStopReason.Cancelled, result.Reason);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task RequestStop_EmergencyCancellation_ReleasesHeldInjectedKey()
    {
        var clock = new FakeClock(1_000_000, 10);
        using var waiterEntered = new ManualResetEventSlim(false);
        var waiter = new FakeWaiter(
            clock,
            (deadline, call, cancellation, _) =>
            {
                if (call == 0)
                {
                    clock.Timestamp = deadline;
                    return DeadlineWaitResult.Reached;
                }

                waiterEntered.Set();
                Assert.True(cancellation.WaitOne(TimeSpan.FromSeconds(5)));
                return DeadlineWaitResult.Cancelled;
            });
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                new ExactWheelInputEvent(
                    0,
                    1,
                    ExactWheelInputEventType.KeyDown,
                    0,
                    0,
                    0x41,
                    0x1E),
                new ExactWheelInputEvent(
                    100_000,
                    2,
                    ExactWheelInputEventType.MouseMove,
                    100,
                    80,
                    0,
                    0)
            ]);

        var playback = engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions { StopOnPhysicalInput = false },
            CancellationToken.None);
        Assert.True(waiterEntered.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        engine.RequestStop();
        var result = await playback;

        Assert.Equal(ExactWheelPlaybackStopReason.Cancelled, result.Reason);
        Assert.True(result.CleanupSucceeded);
        Assert.Equal(2, backend.Batches.Count);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventKeyUp |
            ExactWheelNativeMethods.KeyboardEventScanCode,
            backend.Batches[1][0].Data.Keyboard.Flags);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_TransientTargetWithHeldKeyStopsThenReleases()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                new ExactWheelInputEvent(
                    0,
                    1,
                    ExactWheelInputEventType.KeyDown,
                    0,
                    0,
                    0x41,
                    0x1E),
                new ExactWheelInputEvent(
                    10_000,
                    2,
                    ExactWheelInputEventType.KeyUp,
                    0,
                    0,
                    0x41,
                    0x1E)
            ],
            durationMicroseconds: 10_000);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                EventDispatchAuthorization = inputEvent =>
                    inputEvent.Sequence == 1
                        ? ExactWheelDispatchAuthorization.Authorized
                        : ExactWheelDispatchAuthorization
                            .TemporarilyUnavailable
            },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.TargetLost, result.Reason);
        Assert.True(result.CleanupSucceeded);
        Assert.Contains("global release input", result.Message);
        Assert.Equal(2, backend.Batches.Count);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventKeyUp |
            ExactWheelNativeMethods.KeyboardEventScanCode,
            backend.Batches[1][0].Data.Keyboard.Flags);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_CancelledCleanupFailure_RemainsAnExplicitFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(
            clock,
            (_, call, _, _) =>
            {
                if (call == 0)
                    return DeadlineWaitResult.Reached;
                cancellation.Cancel();
                return DeadlineWaitResult.Cancelled;
            });
        var backend = new FakeInputBackend((inputs, call) =>
            call == 0
                ? (checked((uint)inputs.Length), 0)
                : (0, 5));
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                new ExactWheelInputEvent(
                    0,
                    1,
                    ExactWheelInputEventType.KeyDown,
                    0,
                    0,
                    0x41,
                    0x1E),
                new ExactWheelInputEvent(
                    100_000,
                    2,
                    ExactWheelInputEventType.MouseMove,
                    100,
                    80,
                    0,
                    0)
            ]);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions { StopOnPhysicalInput = false },
            cancellation.Token);

        Assert.Equal(ExactWheelPlaybackStopReason.Cancelled, result.Reason);
        Assert.False(result.CleanupSucceeded);
        Assert.Equal(5, result.Win32Error);
        Assert.Contains("could not release", result.Message);
        Assert.Equal(2, backend.Batches.Count);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_TimerFailure_PreservesErrorCode()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(
            clock,
            (_, _, _, _) => DeadlineWaitResult.Failed)
        {
            Win32Error = 123
        };
        var engine = CreateEngine(clock, waiter, new FakeInputBackend());

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions { StopOnPhysicalInput = false },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.TimerFailed, result.Reason);
        Assert.Equal(123, result.Win32Error);
        await engine.DisposeAsync();
    }

    private static ExactWheelPlaybackEngine CreateEngine(
        FakeClock clock,
        FakeWaiter waiter,
        FakeInputBackend backend) =>
        new(
            new ExactWheelInputInjector(backend),
            clock,
            waiter,
            new FakePhysicalInputState(),
            static () => new FakeCapture());

    private sealed class FakeClock(long frequency, long timestamp) : IPlaybackClock
    {
        public long Frequency { get; } = frequency;

        public long Timestamp { get; set; } = timestamp;
    }

    private sealed class FakeWaiter(
        FakeClock clock,
        Func<long, int, WaitHandle, WaitHandle?, DeadlineWaitResult>? wait = null)
        : IPlaybackWaiter
    {
        internal List<long> Deadlines { get; } = [];

        internal int Win32Error { get; init; }

        public DeadlineWaitResult WaitUntil(
            long deadlineTicks,
            ulong finalSpinMicroseconds,
            IPlaybackClock playbackClock,
            WaitHandle cancellationEvent,
            WaitHandle? interventionEvent,
            out int win32Error)
        {
            _ = finalSpinMicroseconds;
            Assert.Same(clock, playbackClock);
            var call = Deadlines.Count;
            Deadlines.Add(deadlineTicks);
            clock.Timestamp = deadlineTicks;
            win32Error = Win32Error;
            return wait?.Invoke(
                    deadlineTicks,
                    call,
                    cancellationEvent,
                    interventionEvent) ??
                DeadlineWaitResult.Reached;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeInputBackend(
        Func<ExactWheelNativeMethods.NativeInput[], int, (uint Submitted, int Error)>? send = null)
        : IExactWheelInputBackend
    {
        internal List<ExactWheelNativeMethods.NativeInput[]> Batches { get; } = [];

        public uint Send(
            ExactWheelNativeMethods.NativeInput[] inputs,
            out int win32Error)
        {
            Batches.Add(inputs.ToArray());
            var response = send?.Invoke(inputs, Batches.Count - 1) ??
                (checked((uint)inputs.Length), 0);
            win32Error = response.Error;
            return response.Item1;
        }
    }

    private sealed class FakePhysicalInputState : IPhysicalInputState
    {
        internal bool AllReleased { get; set; } = true;

        internal bool ControlReleased { get; init; } = true;

        internal Func<int, bool>? ReleaseProbe { get; init; }

        internal int ReleaseCheckCount { get; private set; }

        public bool AreReleased(IReadOnlyCollection<int> ignoredVirtualKeys)
        {
            _ = ignoredVirtualKeys;
            ReleaseCheckCount++;
            return ReleaseProbe?.Invoke(ReleaseCheckCount) ?? AllReleased;
        }

        public bool AreKeysReleased(IReadOnlyCollection<int> virtualKeys)
        {
            _ = virtualKeys;
            return ControlReleased;
        }
    }

    private sealed class FakeCapture : IExactWheelInputCapture
    {
        internal bool InterventionStarted { get; private set; }

        internal bool InterventionStopped { get; private set; }

        public InputCaptureMode Mode => InputCaptureMode.Idle;

        public void StartRecording(
            int maximumEvents,
            IReadOnlyCollection<int> waitForReleaseVirtualKeys,
            Func<ExactWheelInputEvent, bool>? eventAdmission) =>
            throw new NotSupportedException();

        public InputCaptureResult StopRecording() =>
            throw new NotSupportedException();

        public void StartInterventionMonitor(EventWaitHandle interventionEvent)
        {
            ArgumentNullException.ThrowIfNull(interventionEvent);
            InterventionStarted = true;
        }

        public void StopInterventionMonitor()
        {
            InterventionStopped = true;
        }

        public void Dispose()
        {
        }
    }
}
