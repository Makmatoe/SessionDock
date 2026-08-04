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

    private static MacroDefinition Definition() => new()
    {
        ContentId = "macro-content",
        SafeFileName = "macro.ewmacro",
        Sha256 = new string('A', 64),
        Kind = SessionMacroKind.Client
    };

    private static ExactWheelRecordingTarget Target(int windowLeft)
    {
        var display = Display();
        var metadata = new ExactWheelTargetMetadata(
            "RobloxPlayerBeta",
            "WINDOWSCLIENT",
            new ExactWheelRect(windowLeft, 40, windowLeft + 800, 640),
            new ExactWheelRect(windowLeft + 8, 72, windowLeft + 792, 632));
        return new ExactWheelRecordingTarget((nint)1234, display, metadata);
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

    private static ExactWheelDisplayTopology Display(int width = 1920) => new(
        0,
        0,
        width,
        1080,
        [
            new ExactWheelMonitorSnapshot(
                new ExactWheelRect(0, 0, width, 1080),
                96,
                96)
        ]);
}
