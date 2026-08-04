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
