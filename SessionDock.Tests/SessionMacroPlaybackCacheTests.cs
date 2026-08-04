using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SessionMacroPlaybackCacheTests
{
    [Fact]
    public void GetDisplayTopology_RefreshesPeriodicallyAndOnClockRegression()
    {
        var cache = new SessionMacroPlaybackCache();
        var frequency = 1_000L;
        var captures = 0;

        ExactWheelDisplayTopology Capture()
        {
            captures++;
            return Display(width: 1_900 + captures);
        }

        var initial = cache.GetDisplayTopology(Capture, 10_000, frequency);
        var cached = cache.GetDisplayTopology(Capture, 11_999, frequency);
        var refreshed = cache.GetDisplayTopology(Capture, 12_000, frequency);
        var regressed = cache.GetDisplayTopology(Capture, 1, frequency);

        Assert.Same(initial, cached);
        Assert.NotSame(initial, refreshed);
        Assert.NotSame(refreshed, regressed);
        Assert.Equal(3, captures);
    }

    [Fact]
    public void GetOrLoad_VerifiesAndLoadsOneSourceOncePerRun()
    {
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var recording = Recording(eventCount: 2);
        var loadCount = 0;

        var first = cache.GetOrLoad(definition, _ =>
        {
            loadCount++;
            return recording;
        });
        var second = cache.GetOrLoad(definition, _ =>
        {
            loadCount++;
            return Recording(eventCount: 1);
        });

        Assert.Same(recording, first);
        Assert.Same(first, second);
        Assert.Equal(1, loadCount);
        Assert.Equal(1, cache.CachedSourceCount);
    }

    [Fact]
    public void GetOrLoad_SamePhysicalArtifactIsSharedAcrossDefinitions()
    {
        var cache = new SessionMacroPlaybackCache();
        var firstDefinition = Definition(contentId: "first");
        var aliasDefinition = Definition(contentId: "alias");
        var recording = Recording(eventCount: 2);
        var loadCount = 0;

        var first = cache.GetOrLoad(firstDefinition, _ =>
        {
            loadCount++;
            return recording;
        });
        var alias = cache.GetOrLoad(aliasDefinition, _ =>
        {
            loadCount++;
            return Recording(eventCount: 1);
        });

        Assert.Same(first, alias);
        Assert.Equal(1, loadCount);
        Assert.Equal(1, cache.CachedSourceCount);
    }

    [Fact]
    public void GetOrLoad_MoreThanSixtyFourTinySourcesFitByEventBudget()
    {
        const int sourceCount = 128;
        var cache = new SessionMacroPlaybackCache();
        var recording = Recording(eventCount: 1);
        var definitions = Enumerable.Range(0, sourceCount)
            .Select(index => Definition(
                contentId: $"macro-{index}",
                hashCharacter: (char)('A' + index % 26),
                safeFileName: $"macro-{index}.ewmacro"))
            .ToArray();
        var loadCount = 0;

        foreach (var definition in definitions)
        {
            _ = cache.GetOrLoad(definition, _ =>
            {
                loadCount++;
                return recording;
            });
        }
        foreach (var definition in definitions)
        {
            _ = cache.GetOrLoad(definition, _ =>
            {
                loadCount++;
                return recording;
            });
        }

        Assert.Equal(sourceCount, loadCount);
        Assert.Equal(sourceCount, cache.CachedSourceCount);
    }

    [Fact]
    public void GetOrLoad_AggregateBeyondOneMillionEventsPagesOncePerRun()
    {
        const int sourceCount = 3;
        var modeledEventsPerSource = checked(
            (int)ExactWheelLimits.MaximumEventCount);
        using var cache = new SessionMacroPlaybackCache(
            _ => modeledEventsPerSource);
        var recording = Recording(eventCount: 2);
        var definitions = Enumerable.Range(0, sourceCount)
            .Select(index => Definition(
                contentId: $"macro-{index}",
                hashCharacter: (char)('A' + index),
                safeFileName: $"macro-{index}.ewmacro"))
            .ToArray();
        var loadCount = 0;

        for (var cycle = 0; cycle < 2; cycle++)
        {
            foreach (var definition in definitions)
            {
                _ = cache.GetOrLoad(definition, _ =>
                {
                    loadCount++;
                    return recording;
                });
            }
        }

        Assert.Equal(sourceCount, loadCount);
        Assert.Equal(sourceCount, cache.CachedSourceCount);
        Assert.Equal(
            SessionMacroPlaybackCache.MaximumSourceEvents,
            cache.CachedResidentSourceEventCount);
        Assert.Equal(1, cache.CachedPageableSourceCount);
        Assert.Equal(80, cache.CachedPageableSourceBytes);
    }

    [Fact]
    public void Dispose_ReleasesPageableSourceAndDeletesBackingFile()
    {
        var cache = new SessionMacroPlaybackCache(
            _ => checked((int)ExactWheelLimits.MaximumEventCount));
        _ = cache.GetOrLoad(
            Definition(contentId: "first", safeFileName: "first.ewmacro"),
            _ => Recording(eventCount: 2));
        _ = cache.GetOrLoad(
            Definition(
                contentId: "second",
                hashCharacter: 'B',
                safeFileName: "second.ewmacro"),
            _ => Recording(eventCount: 2));
        var pageableSource = Recording(eventCount: 1_025);
        var pageable = cache.GetOrLoad(
            Definition(
                contentId: "pageable",
                hashCharacter: 'C',
                safeFileName: "pageable.ewmacro"),
            _ => pageableSource);
        var backingPath = Assert.Single(cache.PageableSourcePaths);

        Assert.True(File.Exists(backingPath));
        Assert.Equal(pageableSource.Events[0], pageable.Events[0]);
        Assert.Equal(pageableSource.Events[511], pageable.Events[511]);
        Assert.Equal(pageableSource.Events[512], pageable.Events[512]);
        Assert.Equal(pageableSource.Events[1_024], pageable.Events[1_024]);

        cache.Dispose();

        Assert.False(File.Exists(backingPath));
        Assert.Throws<ObjectDisposedException>(
            () => _ = pageable.Events[0]);
        Assert.Equal(0, cache.CachedSourceCount);
        cache.Dispose();
    }

    [Fact]
    public void GetOrLoad_RejectsArtifactBeyondRunLimitBeforeLoading()
    {
        using var cache = new SessionMacroPlaybackCache();
        var recording = Recording(eventCount: 1);
        for (var index = 0;
             index < SessionMacroPlaybackCache.MaximumSourceArtifacts;
             index++)
        {
            _ = cache.GetOrLoad(
                Definition(
                    contentId: $"macro-{index}",
                    hashCharacter: (char)('A' + index % 26),
                    safeFileName: $"macro-{index}.ewmacro"),
                _ => recording);
        }

        var loadCount = 0;
        Assert.Throws<InvalidDataException>(() => cache.GetOrLoad(
            Definition(
                contentId: "one-too-many",
                hashCharacter: 'Z',
                safeFileName: "one-too-many.ewmacro"),
            _ =>
            {
                loadCount++;
                return recording;
            }));
        Assert.Equal(0, loadCount);
    }

    [Fact]
    public void GetOrLoad_PageableBudgetRejectsBeforeNextSourceLoad()
    {
        const int eventCount = 1_025;
        const long sourceBytes = eventCount * 40L;
        using var cache = new SessionMacroPlaybackCache(
            _ => checked((int)ExactWheelLimits.MaximumEventCount),
            maximumPageableSourceBytes: sourceBytes);
        var recording = Recording(eventCount);
        for (var index = 0; index < 3; index++)
        {
            _ = cache.GetOrLoad(
                Definition(
                    contentId: $"budget-{index}",
                    hashCharacter: (char)('G' + index),
                    safeFileName: $"budget-{index}.ewmacro",
                    eventCount: eventCount),
                _ => recording);
        }
        var existingPath = Assert.Single(cache.PageableSourcePaths);
        var loadCount = 0;

        Assert.Throws<InvalidDataException>(() => cache.GetOrLoad(
            Definition(
                contentId: "budget-rejected",
                hashCharacter: 'J',
                safeFileName: "budget-rejected.ewmacro",
                eventCount: eventCount),
            _ =>
            {
                loadCount++;
                return recording;
            }));

        Assert.Equal(0, loadCount);
        Assert.Equal(sourceBytes, cache.CachedPageableSourceBytes);
        Assert.Equal([existingPath], cache.PageableSourcePaths);
    }

    [Fact]
    public void Reservation_CancelOrErrorBeforeTransferReleasesPageableCache()
    {
        var cache = CreateCacheWithPageableSource(out var pageable);
        var backingPath = Assert.Single(cache.PageableSourcePaths);
        var reservation = new SessionMacroPlaybackCacheReservation(cache);

        reservation.Dispose();

        Assert.False(File.Exists(backingPath));
        Assert.Throws<ObjectDisposedException>(
            () => _ = pageable.Events[0]);
    }

    [Fact]
    public void Reservation_TransferredCacheLivesUntilRunDisposesIt()
    {
        var cache = CreateCacheWithPageableSource(out var pageable);
        var backingPath = Assert.Single(cache.PageableSourcePaths);
        using var reservation = new SessionMacroPlaybackCacheReservation(cache);

        var transferred = Assert.IsType<SessionMacroPlaybackCache>(
            reservation.Take());
        reservation.Dispose();

        Assert.True(File.Exists(backingPath));
        _ = pageable.Events[0];

        transferred.Dispose();
        Assert.False(File.Exists(backingPath));
    }

    [Fact]
    public void FailedPublishedTransfer_ClearsFieldBeforeDisposingCache()
    {
        var transferred = new SessionMacroPlaybackCache();
        SessionMacroPlaybackCache? published = transferred;

        SessionMacroPlaybackCacheReservation.ReleaseFailedTransfer(
            ref published,
            transferred,
            wasPublished: true);

        Assert.Null(published);
        Assert.Throws<ObjectDisposedException>(() =>
            transferred.GetDisplayTopology(
                static () => Display(),
                timestamp: 0,
                frequency: 1));
    }

    [Fact]
    public void FailedPublishedTransfer_DoesNotDisposeConsumedCache()
    {
        var transferred = new SessionMacroPlaybackCache();
        SessionMacroPlaybackCache? published = null;

        SessionMacroPlaybackCacheReservation.ReleaseFailedTransfer(
            ref published,
            transferred,
            wasPublished: true);

        Assert.Null(published);
        _ = transferred.GetDisplayTopology(
            static () => Display(),
            timestamp: 0,
            frequency: 1);
        transferred.Dispose();
    }

    [Fact]
    public void PageableSource_SequentialPlaybackAllocatesNoManagedMemory()
    {
        using var cache = CreateCacheWithPageableSource(out var pageable);
        _ = pageable.Events[0];
        ulong observed = 0;

        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            for (var index = 0; index < pageable.Events.Count; index++)
                observed ^= pageable.Events[index].Sequence;
        });

        Assert.Equal(0, allocated);
        Assert.NotEqual(ulong.MaxValue, observed);
    }

    [Fact]
    public void CoordinatePlan_ReusesExactGeometryAndReplacesMovedHandle()
    {
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var source = Recording(eventCount: 2);
        var firstTarget = Target(windowLeft: 40);
        var movedTarget = Target(windowLeft: 80);
        var transformCount = 0;

        ExactWheelPlaybackCoordinateTransform Create(
            ExactWheelRecording recording,
            ExactWheelRecordingTarget target)
        {
            transformCount++;
            return ExactWheelCoordinateTransforms
                .CreateClientRelativePlaybackTransform(
                    recording,
                    target.Display,
                    target.Metadata);
        }

        var first = Get(firstTarget);
        var repeated = Get(firstTarget);
        var moved = Get(movedTarget);
        var movedRepeated = Get(movedTarget);

        Assert.Same(source, first.Recording);
        Assert.Same(first.CoordinateTransform, repeated.CoordinateTransform);
        Assert.NotSame(
            first.CoordinateTransform,
            moved.CoordinateTransform);
        Assert.Same(
            moved.CoordinateTransform,
            movedRepeated.CoordinateTransform);
        Assert.Equal(2, transformCount);
        Assert.Equal(1, cache.CachedCoordinateTransformCount);

        SessionMacroPlaybackPlan Get(ExactWheelRecordingTarget target) =>
            cache.GetOrLoadAndCreateTransform(
                definition,
                SessionMacroTransformKind.ClientRelative,
                target,
                source,
                static (loaded, _) => loaded,
                Create);
    }

    [Fact]
    public void CoordinatePlan_MonitorDpiParticipatesInStructuralKey()
    {
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var source = Recording(eventCount: 2);
        var standard = Target(windowLeft: 40, dpiX: 96);
        var structurallyEqual = Target(windowLeft: 40, dpiX: 96);
        var scaled = Target(windowLeft: 40, dpiX: 144);
        var transformCount = 0;

        var first = Get(standard);
        var equal = Get(structurallyEqual);
        var differentDpi = Get(scaled);

        Assert.Same(first.CoordinateTransform, equal.CoordinateTransform);
        Assert.NotSame(
            first.CoordinateTransform,
            differentDpi.CoordinateTransform);
        Assert.Equal(2, transformCount);

        SessionMacroPlaybackPlan Get(ExactWheelRecordingTarget target) =>
            cache.GetOrLoadAndCreateTransform(
                definition,
                SessionMacroTransformKind.ClientRelative,
                target,
                source,
                static (loaded, _) => loaded,
                (recording, destination) =>
                {
                    transformCount++;
                    return ExactWheelCoordinateTransforms
                        .CreateClientRelativePlaybackTransform(
                            recording,
                            destination.Display,
                            destination.Metadata);
                });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public void CoordinatePlan_ArbitraryClientWorkingSetIsWarmOnCycleTwo(
        int clientCount)
    {
        const int eventCount = 100_001;
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var source = Recording(eventCount);
        var targets = Enumerable.Range(0, clientCount)
            .Select(index => Target(
                windowLeft: 40 + index,
                windowHandle: 1_234 + index))
            .ToArray();
        var loadCount = 0;
        var transformCount = 0;

        for (var cycle = 0; cycle < 2; cycle++)
        {
            foreach (var target in targets)
            {
                var plan = cache.GetOrLoadAndCreateTransform(
                    definition,
                    SessionMacroTransformKind.ClientRelative,
                    target,
                    source,
                    (loaded, _) =>
                    {
                        loadCount++;
                        return loaded;
                    },
                    (recording, destination) =>
                    {
                        transformCount++;
                        return ExactWheelCoordinateTransforms
                            .CreateClientRelativePlaybackTransform(
                                recording,
                                destination.Display,
                                destination.Metadata);
                    });
                Assert.Same(source, plan.Recording);
            }
        }

        Assert.Equal(1, loadCount);
        Assert.Equal(clientCount, transformCount);
        Assert.Equal(clientCount, cache.CachedCoordinateTransformCount);
    }

    [Fact]
    public void CoordinatePlan_CachedHitDoesNotAllocateOrInvokeCallbacks()
    {
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var target = Target(windowLeft: 40);
        var source = Recording(eventCount: 2);
        _ = cache.GetOrLoadAndCreateTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            target,
            source,
            static (loaded, _) => loaded,
            static (recording, destination) => ExactWheelCoordinateTransforms
                .CreateClientRelativePlaybackTransform(
                    recording,
                    destination.Display,
                    destination.Metadata));
        Func<ExactWheelRecording, MacroDefinition, ExactWheelRecording>
            unexpectedLoad = static (_, _) =>
                throw new InvalidOperationException("Source cache miss.");
        Func<ExactWheelRecording, ExactWheelRecordingTarget,
            ExactWheelPlaybackCoordinateTransform> unexpectedTransform =
            static (_, _) =>
                throw new InvalidOperationException("Plan cache miss.");

        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            for (var index = 0; index < 10_000; index++)
            {
                _ = cache.GetOrLoadAndCreateTransform(
                    definition,
                    SessionMacroTransformKind.ClientRelative,
                    target,
                    source,
                    unexpectedLoad,
                    unexpectedTransform);
            }
        });

        Assert.Equal(0, allocated);
    }

    private static MacroDefinition Definition(
        string contentId = "macro-content",
        char hashCharacter = 'A',
        string safeFileName = "macro.ewmacro",
        int eventCount = 0) => new()
        {
            ContentId = contentId,
            SafeFileName = safeFileName,
            Sha256 = new string(hashCharacter, 64),
            Kind = SessionMacroKind.Client,
            EventCount = eventCount
        };

    private static SessionMacroPlaybackCache CreateCacheWithPageableSource(
        out ExactWheelRecording pageable)
    {
        var cache = new SessionMacroPlaybackCache(
            _ => checked((int)ExactWheelLimits.MaximumEventCount));
        var recording = Recording(eventCount: 1_025);
        for (var index = 0; index < 3; index++)
        {
            var loaded = cache.GetOrLoad(
                Definition(
                    contentId: $"reservation-{index}",
                    hashCharacter: (char)('D' + index),
                    safeFileName: $"reservation-{index}.ewmacro"),
                _ => recording);
            if (index == 2)
                pageable = loaded;
        }

        pageable = cache.GetOrLoad(
            Definition(
                contentId: "reservation-2",
                hashCharacter: 'F',
                safeFileName: "reservation-2.ewmacro"),
            _ => throw new InvalidOperationException());
        return cache;
    }

    private static ExactWheelRecordingTarget Target(
        int windowLeft,
        uint dpiX = 96,
        int windowHandle = 1234)
    {
        var display = Display(dpiX: dpiX);
        var metadata = new ExactWheelTargetMetadata(
            "RobloxPlayerBeta",
            "WINDOWSCLIENT",
            new ExactWheelRect(windowLeft, 40, windowLeft + 800, 640),
            new ExactWheelRect(windowLeft + 8, 72, windowLeft + 792, 632));
        return new ExactWheelRecordingTarget(
            (nint)windowHandle,
            display,
            metadata);
    }

    private static ExactWheelRecording Recording(int eventCount)
    {
        var display = Display();
        var metadata = new ExactWheelTargetMetadata(
            "RobloxPlayerBeta",
            "WINDOWSCLIENT",
            new ExactWheelRect(40, 40, 840, 640),
            new ExactWheelRect(48, 72, 832, 632));
        var events = Enumerable.Range(0, eventCount)
            .Select(index => new ExactWheelInputEvent(
                (ulong)index,
                (ulong)index,
                ExactWheelInputEventType.MouseMove,
                48 + index % 700,
                72 + index % 500,
                0,
                0));
        return new ExactWheelRecording(
            (ulong)eventCount,
            display,
            metadata,
            events);
    }

    private static ExactWheelDisplayTopology Display(
        int width = 1920,
        uint dpiX = 96) => new(
        0,
        0,
        width,
        1080,
        [
            new ExactWheelMonitorSnapshot(
                new ExactWheelRect(0, 0, width, 1080),
                dpiX,
                96)
        ]);
}
