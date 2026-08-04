using SessionDock.ExactWheel;
using SessionDock.ExactWheel.Windows;

namespace SessionDock.Tests;

public sealed class ExactWheelPlaybackEngineTests
{
    [Fact]
    public void PreparePlaybackRecording_CanonicalTimeline_IsAllocationFree()
    {
        const int eventCount = 100_000;
        var events = Enumerable.Range(0, eventCount)
            .Select(index => MouseMove(
                checked((ulong)index * 100),
                checked((ulong)index + 1),
                100 + index % 500))
            .ToArray();
        var recording = ExactWheelTestData.Recording(
            events,
            durationMicroseconds: eventCount * 100UL);
        _ = ExactWheelRecordingValidator.FinalizeOwned(
            ExactWheelTestData.Display(),
            ExactWheelTestData.Target(),
            events,
            eventCount * 100UL);
        ExactWheelRecording? prepared = null;
        var initialAllocated = AllocationMeasurement.MinimumAllocatedBytes(
            () => prepared = ExactWheelPlaybackEngine
                .PreparePlaybackRecording(recording));
        Assert.Same(recording, prepared);
        Assert.InRange(initialAllocated, 0, 256);

        var repeatedAllocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            for (var index = 0; index < 100_000; index++)
            {
                prepared = ExactWheelPlaybackEngine
                    .PreparePlaybackRecording(recording);
            }
        });
        Assert.Same(recording, prepared);
        Assert.InRange(repeatedAllocated, 0, 256);
    }

    [Fact]
    public void PreparePlaybackRecording_UnsortedLegacyInput_IsCanonicalized()
    {
        var recording = ExactWheelTestData.Recording(
            events:
            [
                MouseMove(10_000, 2, 200),
                MouseMove(0, 1, 100)
            ],
            durationMicroseconds: 20_000);

        var prepared = ExactWheelPlaybackEngine
            .PreparePlaybackRecording(recording);

        Assert.NotSame(recording, prepared);
        Assert.Equal(
            [1UL, 2UL],
            prepared.Events.Select(inputEvent => inputEvent.Sequence));
    }

    [Fact]
    public void FinalizeOwned_CanonicalTimeline_DoesNotCopyEventArray()
    {
        const int eventCount = 100_000;
        var events = Enumerable.Range(0, eventCount)
            .Select(index => MouseMove(
                checked((ulong)index * 100),
                checked((ulong)index + 1),
                100 + index % 500))
            .ToArray();
        _ = ExactWheelRecordingValidator.FinalizeOwned(
            ExactWheelTestData.Display(),
            ExactWheelTestData.Target(),
            [MouseMove(0, 1, 100)],
            100);
        var display = ExactWheelTestData.Display();
        var target = ExactWheelTestData.Target();
        ExactWheelRecording? recording = null;
        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
            recording = ExactWheelRecordingValidator.FinalizeOwned(
                display,
                target,
                events,
                eventCount * 100UL));
        Assert.NotNull(recording);
        Assert.Equal(eventCount, recording.Events.Count);
        Assert.InRange(allocated, 0, 256);
    }

    [Fact]
    public void Recording_PublicConstructor_PreservesCallerImmutability()
    {
        var original = MouseMove(0, 1, 100);
        var callerEvents = new[] { original };
        var recording = ExactWheelTestData.Recording(
            callerEvents,
            durationMicroseconds: 100);

        callerEvents[0] = MouseMove(0, 1, 500);

        Assert.Equal(original, recording.Events[0]);
    }

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
    public async Task PlayAsync_CoordinateTransformMapsBeforeAuthorizationAndInjection()
    {
        var clock = new FakeClock(1_000_000, 1_000);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events: [MouseMove(0, 1, 100)],
            durationMicroseconds: 0);
        var destinationDisplay = ExactWheelTestData.Display(
            0,
            0,
            1_920,
            1_080);
        var transform = ExactWheelCoordinateTransforms
            .CreateClientRelativePlaybackTransform(
                recording,
                destinationDisplay,
                ExactWheelTestData.Target(
                    new ExactWheelRect(10, 20, 710, 430)));
        ExactWheelInputEvent? authorizedEvent = null;

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                CoordinateTransform = transform,
                EventDispatchAuthorization = inputEvent =>
                {
                    authorizedEvent = inputEvent;
                    return ExactWheelDispatchAuthorization.Authorized;
                }
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(authorizedEvent);
        Assert.Equal((10, 30), (authorizedEvent.Value.X, authorizedEvent.Value.Y));
        var injected = Assert.Single(Assert.Single(backend.Batches));
        Assert.Equal(
            ExactWheelCoordinateTransforms.NormalizeForSendInput(
                10,
                destinationDisplay.VirtualLeft,
                destinationDisplay.VirtualWidth),
            injected.Data.Mouse.X);
        Assert.Equal(
            ExactWheelCoordinateTransforms.NormalizeForSendInput(
                30,
                destinationDisplay.VirtualTop,
                destinationDisplay.VirtualHeight),
            injected.Data.Mouse.Y);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_TimingLoopStaysOffSharedThreadPool()
    {
        var clock = new FakeClock(1_000_000, 1_000);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(
                events: [MouseMove(0, 1, 100)],
                durationMicroseconds: 1),
            new ExactWheelPlaybackOptions { StopOnPhysicalInput = false },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([false], backend.ThreadPoolDispatches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_ReusedSessionClearsPriorCancellationSignal()
    {
        var clock = new FakeClock(1_000_000, 1_000);
        var waiter = new FakeWaiter(
            clock,
            (_, _, cancellation, _) => cancellation.WaitOne(0)
                ? DeadlineWaitResult.Cancelled
                : DeadlineWaitResult.Reached);
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events: [MouseMove(0, 1, 100)],
            durationMicroseconds: 1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var first = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions { StopOnPhysicalInput = false },
            cancelled.Token);
        var second = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions { StopOnPhysicalInput = false },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.Cancelled, first.Reason);
        Assert.True(second.Succeeded);
        Assert.Single(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_ReusesOneDedicatedWorkerAcrossSegments()
    {
        var clock = new FakeClock(1_000_000, 1_000);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events: [MouseMove(0, 1, 100)],
            durationMicroseconds: 1);
        var options = new ExactWheelPlaybackOptions
        {
            StopOnPhysicalInput = false
        };

        var first = await engine.PlayAsync(
            recording,
            options,
            CancellationToken.None);
        var second = await engine.PlayAsync(
            recording,
            options,
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(2, backend.ThreadIds.Count);
        Assert.Single(backend.ThreadIds.Distinct());
        Assert.All(
            backend.ThreadPoolDispatches,
            isThreadPoolThread => Assert.False(isThreadPoolThread));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_WorkerFaultRejectsRetryBeforePublishingCompletion()
    {
        var backend = new FakeInputBackend();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            new UnexpectedFailureClock(),
            new PassiveWaiter(),
            new FakePhysicalInputState(),
            static () => new FakeCapture());
        var recording = ExactWheelTestData.Recording(
            events: [MouseMove(0, 1, 100)],
            durationMicroseconds: 1);
        var options = new ExactWheelPlaybackOptions
        {
            StopOnPhysicalInput = false
        };

        await Assert.ThrowsAsync<ApplicationException>(() =>
            engine.PlayAsync(
                recording,
                options,
                CancellationToken.None));
        var retry = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = engine.PlayAsync(
                recording,
                options,
                CancellationToken.None);
        });

        Assert.IsType<ApplicationException>(retry.InnerException);
        await Assert.ThrowsAsync<ApplicationException>(async () =>
            await engine.DisposeAsync());
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
        Assert.Equal(14, guardCalls);
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
        var targetAvailable = true;
        var waiter = new FakeWaiter(
            clock,
            (_, _, _, _) =>
            {
                targetAvailable = false;
                return DeadlineWaitResult.Reached;
            });
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
                PreDispatchGuard = () =>
                {
                    guardCalls++;
                    return targetAvailable;
                }
            },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.TargetLost, result.Reason);
        Assert.Equal(1, guardCalls);
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
        Assert.Equal([expectedEvent], guardedEvents);
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
        Assert.Equal(10, guardCalls);
        Assert.Equal(7, backend.Batches.Count);
        Assert.Equal(
            [10L, 10_010L, 10_010L],
            waiter.Deadlines.Take(3));
        Assert.Equal(260_010L, waiter.Deadlines[^1]);
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
            [10L, 10_010L, 20_010L],
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
        Assert.Equal(9, authorizationCalls);
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
        Assert.Equal(9, authorizationCalls);
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
                RecoverFromTimingStalls = false,
                DangerouslyLateMicroseconds = 250_000
            },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.DangerouslyLate, result.Reason);
        Assert.Equal(250_001, result.LatenessMicroseconds);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_SchedulerStall_RebasesAndPreservesTransitions()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(
            clock,
            (deadline, call, _, _) =>
            {
                if (call == 0)
                    clock.Timestamp = deadline + 500_000;
                return DeadlineWaitResult.Reached;
            });
        var backend = new FakeInputBackend();
        var reported = new CollectingProgress();
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                MouseMove(0, 1, 100),
                MouseMove(10_000, 2, 200),
                MouseMove(20_000, 3, 300),
                MouseButton(30_000, 4, down: true),
                MouseButton(40_000, 5, down: false),
                Key(50_000, 6, down: true),
                Key(60_000, 7, down: false)
            ],
            durationMicroseconds: 70_000);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                MaximumCatchUpMicroseconds = 5_000,
                Progress = reported
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(5, backend.Batches.Count);
        Assert.Equal(
            [1, 2, 2, 1, 1],
            backend.Batches.Select(batch => batch.Length));
        Assert.Equal(
            ExactWheelNativeMethods.MouseEventLeftDown,
            backend.Batches[1][1].Data.Mouse.Flags);
        Assert.Equal(
            ExactWheelNativeMethods.MouseEventLeftUp,
            backend.Batches[2][1].Data.Mouse.Flags);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventScanCode,
            backend.Batches[3][0].Data.Keyboard.Flags);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventScanCode |
            ExactWheelNativeMethods.KeyboardEventKeyUp,
            backend.Batches[4][0].Data.Keyboard.Flags);
        Assert.All(
            reported.Values,
            item => Assert.InRange(item.LatenessMicroseconds, 0, 5_000));
        Assert.Equal(2, reported.Values.Count);
        Assert.Equal(2, reported.Values[0].EventIndex);
        Assert.Equal(6, reported.Values[^1].EventIndex);
        Assert.Equal(545_010L, waiter.Deadlines[^1]);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_StallDuringDrag_DoesNotCoalesceHeldMoves()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(
            clock,
            (deadline, call, _, _) =>
            {
                if (call == 1)
                    clock.Timestamp = deadline + 500_000;
                return DeadlineWaitResult.Reached;
            });
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                MouseButton(0, 1, down: true),
                MouseMove(10_000, 2, 200),
                MouseMove(20_000, 3, 300),
                MouseButton(30_000, 4, down: false)
            ],
            durationMicroseconds: 40_000);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                MaximumCatchUpMicroseconds = 5_000
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(4, backend.Batches.Count);
        Assert.Equal(
            [2, 1, 1, 2],
            backend.Batches.Select(batch => batch.Length));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_OverdueMoveBurst_CoalescesBeforeFocusChecks()
    {
        const int eventCount = 10_000;
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(
            clock,
            (deadline, call, _, _) =>
            {
                if (call == 0)
                    clock.Timestamp = deadline + 2_000_000;
                return DeadlineWaitResult.Reached;
            });
        var backend = new FakeInputBackend();
        var guardCalls = 0;
        var engine = CreateEngine(clock, waiter, backend);
        var events = Enumerable.Range(0, eventCount)
            .Select(index => MouseMove(
                checked((ulong)index * 100),
                checked((ulong)index + 1),
                100 + index % 500))
            .ToArray();
        var recording = ExactWheelTestData.Recording(
            events,
            durationMicroseconds: eventCount * 100UL);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                PreDispatchGuard = () =>
                {
                    guardCalls++;
                    return true;
                }
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Single(backend.Batches);
        Assert.Equal(1, guardCalls);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_HighRateSlowDispatch_ResynchronizesWithoutStopping()
    {
        const int keyPressCount = 300;
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend((inputs, _) =>
        {
            clock.Timestamp += 2_000;
            return (checked((uint)inputs.Length), 0);
        });
        var engine = CreateEngine(clock, waiter, backend);
        var events = Enumerable.Range(0, keyPressCount)
            .SelectMany(index => new[]
            {
                Key(
                    checked((ulong)index * 20_000),
                    checked((ulong)index * 2 + 1),
                    down: true),
                Key(
                    checked((ulong)index * 20_000 + 10_000),
                    checked((ulong)index * 2 + 2),
                    down: false)
            })
            .ToArray();
        var recording = ExactWheelTestData.Recording(
            events,
            durationMicroseconds: keyPressCount * 20_000UL);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                Rate = ExactWheelPlaybackRate.FromRatio(100, 1)
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(keyPressCount * 2, backend.Batches.Count);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventScanCode,
            backend.Batches[0][0].Data.Keyboard.Flags);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventScanCode |
            ExactWheelNativeMethods.KeyboardEventKeyUp,
            backend.Batches[^1][0].Data.Keyboard.Flags);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_AuthorizationStall_RebasesBeforeInjection()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var injectionTimestamps = new List<long>();
        var backend = new FakeInputBackend((inputs, _) =>
        {
            injectionTimestamps.Add(clock.Timestamp);
            return (checked((uint)inputs.Length), 0);
        });
        var authorizationCalls = 0;
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events:
            [
                Key(0, 1, down: true),
                Key(100_000, 2, down: false)
            ],
            durationMicroseconds: 100_000);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                MaximumCatchUpMicroseconds = 0,
                EventDispatchAuthorization = _ =>
                {
                    if (authorizationCalls++ == 0)
                        clock.Timestamp += 500_000;
                    return ExactWheelDispatchAuthorization.Authorized;
                }
            },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, authorizationCalls);
        Assert.Equal([500_010L, 600_010L], injectionTimestamps);
        Assert.Equal(2, backend.Batches.Count);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventScanCode,
            backend.Batches[0][0].Data.Keyboard.Flags);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventScanCode |
            ExactWheelNativeMethods.KeyboardEventKeyUp,
            backend.Batches[1][0].Data.Keyboard.Flags);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_AuthorizationStall_StillEnforcesLateLimit()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var engine = CreateEngine(clock, waiter, backend);
        var recording = ExactWheelTestData.Recording(
            events: [Key(0, 1, down: true)],
            durationMicroseconds: 10_000);

        var result = await engine.PlayAsync(
            recording,
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                RecoverFromTimingStalls = false,
                DangerouslyLateMicroseconds = 250_000,
                EventDispatchAuthorization = _ =>
                {
                    clock.Timestamp += 300_000;
                    return ExactWheelDispatchAuthorization.Authorized;
                }
            },
            CancellationToken.None);

        Assert.Equal(
            ExactWheelPlaybackStopReason.DangerouslyLate,
            result.Reason);
        Assert.Equal(300_000, result.LatenessMicroseconds);
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
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                PauseOnPhysicalInput = true
            },
            CancellationToken.None);

        Assert.Equal(ExactWheelPlaybackStopReason.PhysicalInputHeld, result.Reason);
        Assert.False(capture.InterventionStarted);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_PhysicalInputPressedDuringMonitorStartup_StopsBeforeInjection()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var capture = new FakeCapture();
        var physicalInput = new FakePhysicalInputState
        {
            ReleaseProbe = call => call == 1
        };
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
                PauseOnPhysicalInput = true
            },
            CancellationToken.None);

        Assert.Equal(
            ExactWheelPlaybackStopReason.PhysicalInputHeld,
            result.Reason);
        Assert.Equal(2, physicalInput.ReleaseCheckCount);
        Assert.True(capture.InterventionStarted);
        Assert.True(capture.InterventionStopped);
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
        Assert.Equal(1, capture.StartCount);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task InterventionSequence_ReusesOneMonitorAcrossSerialPlayback()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(clock);
        var backend = new FakeInputBackend();
        var capture = new FakeCapture();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            waiter,
            new FakePhysicalInputState(),
            () => capture);
        var sequence = engine.BeginInterventionSequence();

        var first = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions(),
            CancellationToken.None);
        var second = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions(),
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(InputCaptureMode.Intervention, capture.Mode);
        Assert.Equal(1, capture.StartCount);
        Assert.Equal(0, capture.StopCount);
        Assert.Equal(0, capture.DisposeCount);

        await sequence.DisposeAsync();

        Assert.Equal(InputCaptureMode.Idle, capture.Mode);
        Assert.Equal(1, capture.StartCount);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, capture.DisposeCount);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task InterventionSequence_ConcurrentDisposalAwaitsOneHookTeardown()
    {
        using var stopEntered = new ManualResetEventSlim(false);
        using var allowStop = new ManualResetEventSlim(false);
        var clock = new FakeClock(1_000_000, 10);
        var capture = new FakeCapture
        {
            StopEntered = stopEntered,
            AllowStop = allowStop
        };
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(new FakeInputBackend()),
            clock,
            new FakeWaiter(clock),
            new FakePhysicalInputState(),
            () => capture);
        var sequence = engine.BeginInterventionSequence();

        var firstDispose = sequence.DisposeAsync().AsTask();
        Assert.True(stopEntered.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        var secondDispose = sequence.DisposeAsync().AsTask();
        var engineDispose = engine.DisposeAsync().AsTask();

        Assert.Same(firstDispose, secondDispose);
        Assert.False(secondDispose.IsCompleted);
        Assert.False(engineDispose.IsCompleted);

        allowStop.Set();
        await Task.WhenAll(firstDispose, secondDispose, engineDispose);

        Assert.Equal(1, capture.StartCount);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public async Task InterventionSequence_RetainsPhysicalSignalBetweenSegments()
    {
        var clock = new FakeClock(1_000_000, 10);
        var waiter = new FakeWaiter(
            clock,
            (_, _, _, intervention) =>
                intervention?.WaitOne(0) == true
                    ? DeadlineWaitResult.PhysicalIntervention
                    : DeadlineWaitResult.Reached);
        var capture = new FakeCapture();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(new FakeInputBackend()),
            clock,
            waiter,
            new FakePhysicalInputState(),
            () => capture);
        await using var sequence = engine.BeginInterventionSequence();

        var first = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions(),
            CancellationToken.None);
        capture.SignalIntervention();
        var second = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions(),
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal(
            ExactWheelPlaybackStopReason.PhysicalIntervention,
            second.Reason);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task InterventionSequence_InitialHeldInputIsRejected()
    {
        var clock = new FakeClock(1_000_000, 10);
        var backend = new FakeInputBackend();
        var capture = new FakeCapture();
        var physicalInput = new FakePhysicalInputState
        {
            AllReleased = false
        };
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            new FakeWaiter(clock),
            physicalInput,
            () => capture);
        var sequence = engine.BeginInterventionSequence();

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(
                events: [MouseMove(0, 1, 100)],
                durationMicroseconds: 0),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                PauseOnPhysicalInput = true
            },
            CancellationToken.None);

        Assert.Equal(
            ExactWheelPlaybackStopReason.PhysicalInputHeld,
            result.Reason);
        Assert.Empty(backend.Batches);
        await sequence.DisposeAsync();
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task InterventionSequence_HeldInputBetweenSegmentsPausesUntilRelease()
    {
        using var pauseObserved = new ManualResetEventSlim(false);
        using var allowRelease = new ManualResetEventSlim(false);
        var clock = new FakeClock(1_000_000, 10);
        var backend = new FakeInputBackend();
        var capture = new FakeCapture();
        var physicalInput = new FakePhysicalInputState();
        var waiter = new FakeWaiter(
            clock,
            (_, _, _, intervention) =>
            {
                if (intervention?.WaitOne(0) == true)
                {
                    pauseObserved.Set();
                    return DeadlineWaitResult.PhysicalIntervention;
                }
                if (pauseObserved.IsSet && !physicalInput.AllReleased)
                {
                    if (!allowRelease.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException(
                            "Synthetic held input was not released.");
                    }
                }
                return DeadlineWaitResult.Reached;
            });
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            waiter,
            physicalInput,
            () => capture);
        var sequence = engine.BeginInterventionSequence();
        var recording = ExactWheelTestData.Recording(
            events: [MouseMove(0, 1, 100)],
            durationMicroseconds: 0);
        var options = new ExactWheelPlaybackOptions
        {
            StopOnPhysicalInput = false,
            PauseOnPhysicalInput = true
        };

        var first = await engine.PlayAsync(
            recording,
            options,
            CancellationToken.None);
        physicalInput.AllReleased = false;
        capture.SignalIntervention();
        var secondPlayback = engine.PlayAsync(
            recording,
            options,
            CancellationToken.None);

        Assert.True(pauseObserved.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        Assert.False(secondPlayback.IsCompleted);
        Assert.Single(backend.Batches);

        physicalInput.AllReleased = true;
        allowRelease.Set();
        var second = await secondPlayback;

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(2, backend.Batches.Count);
        await sequence.DisposeAsync();
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task InterventionSequence_FocusTransitionWaitsForReleasedInputAndExplicitTargetRestore()
    {
        using var physicalReleased = new ManualResetEventSlim(true);
        using var targetRestored = new ManualResetEventSlim(false);
        using var authorizationObserved = new ManualResetEventSlim(false);
        var authorizationCalls = 0;
        var clock = new FakeClock(1_000_000, 10);
        var capture = new FakeCapture();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(new FakeInputBackend()),
            clock,
            new FakeWaiter(clock),
            new FakePhysicalInputState
            {
                ReleaseProbe = _ => physicalReleased.IsSet
            },
            () => capture);
        await using var sequence = engine.BeginInterventionSequence();

        var ordinaryTransition = await engine.WaitForFocusTransitionAsync(
            () =>
            {
                Interlocked.Increment(ref authorizationCalls);
                return ExactWheelDispatchAuthorization.Denied;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ExactWheelFocusTransitionWaitResult.Ready,
            ordinaryTransition);
        Assert.Equal(0, Volatile.Read(ref authorizationCalls));

        physicalReleased.Reset();
        capture.SignalIntervention();
        var focusCallbackCalls = 0;
        var guardedTransition = WaitThenFocusAsync();

        async Task<ExactWheelFocusTransitionWaitResult> WaitThenFocusAsync()
        {
            var result = await engine.WaitForFocusTransitionAsync(
                () =>
                {
                    Interlocked.Increment(ref authorizationCalls);
                    authorizationObserved.Set();
                    return targetRestored.IsSet
                        ? ExactWheelDispatchAuthorization.Authorized
                        : ExactWheelDispatchAuthorization
                            .TemporarilyUnavailable;
                },
                TestContext.Current.CancellationToken);
            if (result == ExactWheelFocusTransitionWaitResult.Ready)
                Interlocked.Increment(ref focusCallbackCalls);
            return result;
        }

        await Task.Delay(
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);
        Assert.False(guardedTransition.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref authorizationCalls));
        Assert.Equal(0, Volatile.Read(ref focusCallbackCalls));
        Assert.False(engine.IsProgrammaticFocusAllowed());

        physicalReleased.Set();
        Assert.True(authorizationObserved.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        Assert.False(guardedTransition.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref focusCallbackCalls));
        Assert.False(engine.IsProgrammaticFocusAllowed());

        targetRestored.Set();
        var resumed = await guardedTransition.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExactWheelFocusTransitionWaitResult.Ready, resumed);
        Assert.Equal(1, Volatile.Read(ref focusCallbackCalls));
        Assert.True(engine.IsProgrammaticFocusAllowed());
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task InterventionSequence_FocusTransitionRejectsInitiallyHeldInputWithoutAuthorization()
    {
        var authorizationCalls = 0;
        var clock = new FakeClock(1_000_000, 10);
        var capture = new FakeCapture();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(new FakeInputBackend()),
            clock,
            new FakeWaiter(clock),
            new FakePhysicalInputState { AllReleased = false },
            () => capture);
        await using var sequence = engine.BeginInterventionSequence();

        var result = await engine.WaitForFocusTransitionAsync(
            () =>
            {
                Interlocked.Increment(ref authorizationCalls);
                return ExactWheelDispatchAuthorization.Authorized;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ExactWheelFocusTransitionWaitResult.PhysicalInputHeld,
            result);
        Assert.Equal(0, Volatile.Read(ref authorizationCalls));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task InterventionSequence_RejectsHeldControlKeyArmingSemantics()
    {
        var clock = new FakeClock(1_000_000, 10);
        var capture = new FakeCapture();
        var backend = new FakeInputBackend();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            new FakeWaiter(clock),
            new FakePhysicalInputState { ControlReleased = false },
            () => capture);
        await using var sequence = engine.BeginInterventionSequence();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.PlayAsync(
                ExactWheelTestData.Recording(),
                new ExactWheelPlaybackOptions
                {
                    WaitForReleaseVirtualKeys = [0x78]
                },
                CancellationToken.None));

        Assert.Contains("retained playback sequence", exception.Message);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task InterventionSequence_RuntimeHookFailureStopsBeforeInjection()
    {
        var clock = new FakeClock(1_000_000, 10);
        var capture = new FakeCapture();
        var waiter = new FakeWaiter(
            clock,
            (_, _, _, _) =>
            {
                capture.SimulateUnexpectedStop();
                return DeadlineWaitResult.PhysicalIntervention;
            });
        var backend = new FakeInputBackend();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            waiter,
            new FakePhysicalInputState(),
            () => capture);
        await using var sequence = engine.BeginInterventionSequence();

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                PauseOnPhysicalInput = true
            },
            CancellationToken.None);

        Assert.Equal(
            ExactWheelPlaybackStopReason.PhysicalIntervention,
            result.Reason);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task PlayAsync_RuntimeHookFailureWhileWaitingForControlReleaseStops()
    {
        var clock = new FakeClock(1_000_000, 10);
        var capture = new TrackedFakeCapture(controlKeysReleased: false);
        var backend = new FakeInputBackend();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            new FakeWaiter(clock),
            new FakePhysicalInputState(),
            () => capture);
        var playback = engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                PauseOnPhysicalInput = true,
                WaitForReleaseVirtualKeys = [0x78]
            },
            CancellationToken.None);
        Assert.True(capture.ReleaseCheckObserved.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        capture.SimulateUnexpectedStop();

        var result = await playback;

        Assert.Equal(
            ExactWheelPlaybackStopReason.PhysicalIntervention,
            result.Reason);
        Assert.Equal(1, capture.StopCount);
        Assert.Equal(1, capture.DisposeCount);
        Assert.Empty(backend.Batches);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task InterventionSequence_HookFailureDuringFinalAuthorizationPreventsInjection()
    {
        var clock = new FakeClock(1_000_000, 10);
        var capture = new FakeCapture();
        var backend = new FakeInputBackend();
        var engine = new ExactWheelPlaybackEngine(
            new ExactWheelInputInjector(backend),
            clock,
            new FakeWaiter(clock),
            new FakePhysicalInputState(),
            () => capture);
        await using var sequence = engine.BeginInterventionSequence();

        var result = await engine.PlayAsync(
            ExactWheelTestData.Recording(),
            new ExactWheelPlaybackOptions
            {
                StopOnPhysicalInput = false,
                PauseOnPhysicalInput = true,
                EventDispatchAuthorization = _ =>
                {
                    capture.SimulateUnexpectedStop();
                    return ExactWheelDispatchAuthorization.Authorized;
                }
            },
            CancellationToken.None);

        Assert.Equal(
            ExactWheelPlaybackStopReason.PhysicalIntervention,
            result.Reason);
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

    private static ExactWheelInputEvent MouseMove(
        ulong timestamp,
        ulong sequence,
        int x) =>
        new(
            timestamp,
            sequence,
            ExactWheelInputEventType.MouseMove,
            x,
            100,
            0,
            0);

    private static ExactWheelInputEvent MouseButton(
        ulong timestamp,
        ulong sequence,
        bool down) =>
        new(
            timestamp,
            sequence,
            down
                ? ExactWheelInputEventType.MouseButtonDown
                : ExactWheelInputEventType.MouseButtonUp,
            400,
            300,
            (int)ExactWheelMouseButton.Left,
            0);

    private static ExactWheelInputEvent Key(
        ulong timestamp,
        ulong sequence,
        bool down) =>
        new(
            timestamp,
            sequence,
            down
                ? ExactWheelInputEventType.KeyDown
                : ExactWheelInputEventType.KeyUp,
            0,
            0,
            0x41,
            0x1E);

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

    private sealed class UnexpectedFailureClock : IPlaybackClock
    {
        public long Frequency =>
            throw new ApplicationException("Synthetic worker failure.");

        public long Timestamp => 1;
    }

    private sealed class PassiveWaiter : IPlaybackWaiter
    {
        public DeadlineWaitResult WaitUntil(
            long deadlineTicks,
            ulong finalSpinMicroseconds,
            IPlaybackClock clock,
            WaitHandle cancellationEvent,
            WaitHandle? interventionEvent,
            out int win32Error)
        {
            _ = deadlineTicks;
            _ = finalSpinMicroseconds;
            _ = clock;
            _ = cancellationEvent;
            _ = interventionEvent;
            win32Error = 0;
            return DeadlineWaitResult.Reached;
        }

        public void Dispose()
        {
        }
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
            clock.Timestamp = Math.Max(clock.Timestamp, deadlineTicks);
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

        internal List<bool> ThreadPoolDispatches { get; } = [];

        internal List<int> ThreadIds { get; } = [];

        public uint Send(
            ExactWheelNativeMethods.NativeInput[] inputs,
            out int win32Error)
        {
            ThreadPoolDispatches.Add(Thread.CurrentThread.IsThreadPoolThread);
            ThreadIds.Add(Environment.CurrentManagedThreadId);
            Batches.Add(inputs.ToArray());
            var response = send?.Invoke(inputs, Batches.Count - 1) ??
                (checked((uint)inputs.Length), 0);
            win32Error = response.Error;
            return response.Item1;
        }
    }

    private sealed class CollectingProgress :
        IProgress<ExactWheelPlaybackProgress>
    {
        internal List<ExactWheelPlaybackProgress> Values { get; } = [];

        public void Report(ExactWheelPlaybackProgress value) =>
            Values.Add(value);
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
        private InputCaptureMode _mode;

        internal bool InterventionStarted => StartCount != 0;

        internal bool InterventionStopped => StopCount != 0;

        internal int StartCount { get; private set; }

        internal int StopCount { get; private set; }

        internal int DisposeCount { get; private set; }

        internal ManualResetEventSlim? StopEntered { get; init; }

        internal ManualResetEventSlim? AllowStop { get; init; }

        private EventWaitHandle? InterventionEvent { get; set; }

        public InputCaptureMode Mode => _mode;

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
            StartCount++;
            InterventionEvent = interventionEvent;
            _mode = InputCaptureMode.Intervention;
        }

        public void StopInterventionMonitor()
        {
            StopEntered?.Set();
            if (AllowStop is not null &&
                !AllowStop.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "Synthetic intervention teardown was not released.");
            }
            StopCount++;
            InterventionEvent = null;
            _mode = InputCaptureMode.Idle;
        }

        public void Dispose()
        {
            DisposeCount++;
            InterventionEvent = null;
            _mode = InputCaptureMode.Idle;
        }

        internal void SignalIntervention() => InterventionEvent?.Set();

        internal void SimulateUnexpectedStop()
        {
            InterventionEvent?.Set();
            _mode = InputCaptureMode.Idle;
        }
    }

    private sealed class TrackedFakeCapture(bool controlKeysReleased) :
        IExactWheelInputCapture,
        ITrackedPhysicalInputState
    {
        private InputCaptureMode _mode;

        internal ManualResetEventSlim ReleaseCheckObserved { get; } =
            new(initialState: false);

        internal int StopCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public InputCaptureMode Mode => _mode;

        public bool AreReleased(
            IReadOnlyCollection<int> ignoredVirtualKeys)
        {
            _ = ignoredVirtualKeys;
            return true;
        }

        public bool AreKeysReleased(
            IReadOnlyCollection<int> virtualKeys)
        {
            _ = virtualKeys;
            ReleaseCheckObserved.Set();
            return controlKeysReleased;
        }

        public void StartRecording(
            int maximumEvents,
            IReadOnlyCollection<int> waitForReleaseVirtualKeys,
            Func<ExactWheelInputEvent, bool>? eventAdmission) =>
            throw new NotSupportedException();

        public InputCaptureResult StopRecording() =>
            throw new NotSupportedException();

        public void StartInterventionMonitor(
            EventWaitHandle interventionEvent)
        {
            ArgumentNullException.ThrowIfNull(interventionEvent);
            _mode = InputCaptureMode.Intervention;
        }

        public void StopInterventionMonitor()
        {
            StopCount++;
            _mode = InputCaptureMode.Idle;
        }

        public void Dispose()
        {
            DisposeCount++;
            _mode = InputCaptureMode.Idle;
            ReleaseCheckObserved.Dispose();
        }

        internal void SimulateUnexpectedStop() =>
            _mode = InputCaptureMode.Idle;
    }
}
