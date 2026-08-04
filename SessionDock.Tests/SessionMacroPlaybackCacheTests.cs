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
    public void GetOrTransform_ReusesOnlyAnExactWindowAndDisplayGeometry()
    {
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var firstTarget = Target(windowLeft: 40);
        var movedTarget = Target(windowLeft: 80);
        var transformCount = 0;

        ExactWheelRecording Transform()
        {
            transformCount++;
            return Recording(eventCount: 2);
        }

        var first = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            firstTarget,
            Transform);
        var repeated = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            firstTarget,
            Transform);
        var moved = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            movedTarget,
            Transform);
        var movedRepeated = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            movedTarget,
            Transform);

        Assert.Same(first, repeated);
        Assert.Same(moved, movedRepeated);
        Assert.NotSame(first, moved);
        Assert.Equal(2, transformCount);
        // Only the current geometry for one HWND is retained.
        Assert.Equal(1, cache.CachedTransformedCount);
    }

    [Fact]
    public void GetOrTransform_MonitorDpiParticipatesInStructuralCacheKey()
    {
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var standard = Target(windowLeft: 40, dpiX: 96);
        var structurallyEqual = Target(windowLeft: 40, dpiX: 96);
        var scaled = Target(windowLeft: 40, dpiX: 144);
        var transformCount = 0;

        ExactWheelRecording Transform()
        {
            transformCount++;
            return Recording(eventCount: 2);
        }

        var first = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            standard,
            Transform);
        var equal = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            structurallyEqual,
            Transform);
        var differentDpi = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            scaled,
            Transform);

        Assert.Same(first, equal);
        Assert.NotSame(first, differentDpi);
        Assert.Equal(2, transformCount);
    }

    [Fact]
    public void GetOrTransform_CachedStructuralHitDoesNotAllocate()
    {
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var target = Target(windowLeft: 40);
        var recording = Recording(eventCount: 2);
        _ = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            target,
            () => recording);
        Func<ExactWheelRecording> unexpectedTransform = () =>
            throw new InvalidOperationException("Cache miss.");
        for (var index = 0; index < 100; index++)
        {
            _ = cache.GetOrTransform(
                definition,
                SessionMacroTransformKind.ClientRelative,
                target,
                unexpectedTransform);
        }
        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            for (var index = 0; index < 10_000; index++)
            {
                _ = cache.GetOrTransform(
                    definition,
                    SessionMacroTransformKind.ClientRelative,
                    target,
                    unexpectedTransform);
            }
        });
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void StatefulSourceAndTransformHits_DoNotAllocateCallbacks()
    {
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var target = Target(windowLeft: 40);
        var recording = Recording(eventCount: 2);
        _ = cache.GetOrLoad(
            definition,
            recording,
            static (loaded, _) => loaded);
        _ = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            target,
            recording,
            static (transformed, _) => transformed);
        Func<ExactWheelRecording, MacroDefinition, ExactWheelRecording>
            sourceHit = static (loaded, _) => loaded;
        Func<ExactWheelRecording, ExactWheelRecordingTarget, ExactWheelRecording>
            transformHit = static (transformed, _) => transformed;
        for (var index = 0; index < 100; index++)
        {
            _ = cache.GetOrLoad(
                definition,
                recording,
                sourceHit);
            _ = cache.GetOrTransform(
                definition,
                SessionMacroTransformKind.ClientRelative,
                target,
                recording,
                transformHit);
        }
        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            for (var index = 0; index < 10_000; index++)
            {
                _ = cache.GetOrLoad(
                    definition,
                    recording,
                    sourceHit);
                _ = cache.GetOrTransform(
                    definition,
                    SessionMacroTransformKind.ClientRelative,
                    target,
                    recording,
                    transformHit);
            }
        });
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void GetOrLoadAndTransform_TransformHitSkipsUncachedLargeSource()
    {
        var cache = new SessionMacroPlaybackCache();
        var firstDefinition = Definition("ew-client-first", 'A');
        var secondDefinition = Definition("ew-client-second", 'B');
        var target = Target(windowLeft: 40);
        var largeSource = Recording(eventCount: 400_000);
        var transformed = Recording(eventCount: 2);
        _ = cache.GetOrLoad(
            firstDefinition,
            largeSource,
            static (recording, _) => recording);
        var loadCount = 0;
        var transformCount = 0;

        ExactWheelRecording LoadLarge(
            MacroDefinition _)
        {
            loadCount++;
            return largeSource;
        }

        ExactWheelRecording Transform(
            ExactWheelRecording _,
            ExactWheelRecordingTarget __)
        {
            transformCount++;
            return transformed;
        }

        var first = cache.GetOrLoadAndTransform(
            secondDefinition,
            SessionMacroTransformKind.ClientRelative,
            target,
            (Func<MacroDefinition, ExactWheelRecording>)LoadLarge,
            static (loader, definition) => loader(definition),
            Transform);
        var repeated = cache.GetOrLoadAndTransform(
            secondDefinition,
            SessionMacroTransformKind.ClientRelative,
            target,
            (Func<MacroDefinition, ExactWheelRecording>)LoadLarge,
            static (loader, definition) => loader(definition),
            Transform);

        Assert.Same(transformed, first);
        Assert.Same(first, repeated);
        Assert.Equal(1, loadCount);
        Assert.Equal(1, transformCount);
        Assert.Equal(1, cache.CachedSourceCount);
        Assert.Equal(1, cache.CachedTransformedCount);
    }

    [Fact]
    public void GetOrTransform_RetainsValidMacroAboveOldHundredThousandLimit()
    {
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var target = Target(windowLeft: 40);
        var large = Recording(eventCount: 100_001);
        var transformCount = 0;

        var first = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            target,
            () =>
            {
                transformCount++;
                return large;
            });
        var repeated = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            target,
            () =>
            {
                transformCount++;
                return Recording(eventCount: 1);
            });

        Assert.Same(first, repeated);
        Assert.Equal(1, transformCount);
        Assert.Equal(1, cache.CachedTransformedCount);
    }

    [Fact]
    public void GetOrTransform_EightClientWorkingSetHitsOnSecondCycle()
    {
        const int eventCount = 100_001;
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var large = Recording(eventCount);
        var targets = Enumerable.Range(0, 8)
            .Select(index => Target(
                windowLeft: 40 + index,
                windowHandle: 1234 + index))
            .ToArray();
        var transformCount = 0;

        foreach (var target in targets)
        {
            _ = cache.GetOrTransform(
                definition,
                SessionMacroTransformKind.ClientRelative,
                target,
                () =>
                {
                    transformCount++;
                    return large;
                });
        }
        foreach (var target in targets)
        {
            _ = cache.GetOrTransform(
                definition,
                SessionMacroTransformKind.ClientRelative,
                target,
                () =>
                {
                    transformCount++;
                    return large;
                });
        }

        Assert.Equal(8, transformCount);
        Assert.Equal(8, cache.CachedTransformedCount);
    }

    [Fact]
    public void GetOrTransform_OverBudgetScanKeepsAdmittedEntries()
    {
        const int eventCount = 400_000;
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var large = Recording(eventCount);
        var targets = Enumerable.Range(0, 3)
            .Select(index => Target(
                windowLeft: 40 + index,
                windowHandle: 1234 + index))
            .ToArray();
        var transformCount = 0;

        for (var cycle = 0; cycle < 2; cycle++)
        {
            foreach (var target in targets)
            {
                _ = cache.GetOrTransform(
                    definition,
                    SessionMacroTransformKind.ClientRelative,
                    target,
                    () =>
                    {
                        transformCount++;
                        return large;
                    });
            }
        }

        Assert.Equal(4, transformCount);
        Assert.Equal(2, cache.CachedTransformedCount);
    }

    [Fact]
    public void GetOrTransform_DoesNotRetainAnOversizedRecording()
    {
        var cache = new SessionMacroPlaybackCache();
        var definition = Definition();
        var target = Target(windowLeft: 40);
        var oversized = Recording(
            SessionMacroPlaybackCache.MaximumEventsPerTransformedEntry + 1);
        var transformCount = 0;

        _ = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            target,
            () =>
            {
                transformCount++;
                return oversized;
            });
        _ = cache.GetOrTransform(
            definition,
            SessionMacroTransformKind.ClientRelative,
            target,
            () =>
            {
                transformCount++;
                return oversized;
            });

        Assert.Equal(2, transformCount);
        Assert.Equal(0, cache.CachedTransformedCount);
    }

    private static MacroDefinition Definition(
        string contentId = "macro-content",
        char hashCharacter = 'A') => new()
        {
            ContentId = contentId,
            SafeFileName = "macro.ewmacro",
            Sha256 = new string(hashCharacter, 64),
            Kind = SessionMacroKind.Client
        };

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
                index,
                index,
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
