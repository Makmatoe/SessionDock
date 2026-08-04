using System.Diagnostics;
using System.IO;
using SessionDock.ExactWheel;
using SessionDock.Models;

namespace SessionDock.Services;

internal enum SessionMacroTransformKind
{
    ClientRelative,
    WholeLayout
}

/// <summary>
/// Keeps verified and coordinate-adapted macros in memory for one playback run.
/// The cache is deliberately bounded: ordinary macros avoid disk and transform
/// work on every loop, while unusually large recordings safely fall back to
/// one-shot processing instead of consuming unbounded laptop memory.
/// </summary>
internal sealed class SessionMacroPlaybackCache
{
    internal const int MaximumSourceEntries = 64;
    internal const int MaximumTransformedEntries = 64;
    internal const int MaximumSourceEvents = 750_000;
    // Eight 100k-event client transforms fit together, matching the common
    // multi-client workload without allowing the cache to grow unbounded.
    internal const int MaximumTransformedEvents = 1_000_000;
    internal const int MaximumEventsPerTransformedEntry =
        (int)ExactWheelLimits.MaximumEventCount;
    internal static readonly TimeSpan DisplayRefreshInterval =
        TimeSpan.FromSeconds(2);

    private readonly Dictionary<MacroCacheKey, ExactWheelRecording> _sources =
        [];
    private readonly Dictionary<TransformCacheKey, TransformedCacheEntry>
        _transformed = new(TransformCacheKeyComparer.Instance);
    private int _sourceEventCount;
    private int _transformedEventCount;
    private ExactWheelDisplayTopology? _displayTopology;
    private long _lastDisplayTimestamp = -1;
    private long _nextDisplayRefreshTimestamp;

    internal ExactWheelDisplayTopology GetDisplayTopology(
        Func<ExactWheelDisplayTopology> capture) =>
        GetDisplayTopology(
            capture,
            Stopwatch.GetTimestamp(),
            Stopwatch.Frequency);

    internal ExactWheelDisplayTopology GetDisplayTopology(
        Func<ExactWheelDisplayTopology> capture,
        long timestamp,
        long frequency)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentOutOfRangeException.ThrowIfNegative(timestamp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        if (_displayTopology is not null &&
            timestamp >= _lastDisplayTimestamp &&
            timestamp < _nextDisplayRefreshTimestamp)
        {
            return _displayTopology;
        }

        var refreshed = capture() ??
            throw new InvalidDataException(
                "The display capture returned no topology.");
        var refreshTicks = checked(
            DisplayRefreshInterval.Ticks * frequency /
            TimeSpan.TicksPerSecond);
        _displayTopology = refreshed;
        _lastDisplayTimestamp = timestamp;
        _nextDisplayRefreshTimestamp = timestamp > long.MaxValue - refreshTicks
            ? long.MaxValue
            : timestamp + refreshTicks;
        return refreshed;
    }

    internal ExactWheelRecording GetOrLoad(
        MacroDefinition definition,
        Func<MacroDefinition, ExactWheelRecording> loader) =>
        GetOrLoad(
            definition,
            loader,
            static (callback, candidate) => callback(candidate));

    internal ExactWheelRecording GetOrLoad<TState>(
        MacroDefinition definition,
        TState state,
        Func<TState, MacroDefinition, ExactWheelRecording> loader)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(loader);
        var key = MacroCacheKey.Create(definition);
        if (_sources.TryGetValue(key, out var cached))
            return cached;

        var recording = loader(state, definition) ??
            throw new InvalidDataException("The macro loader returned no recording.");
        TryCacheSource(key, recording);
        return recording;
    }

    internal ExactWheelRecording GetOrTransform(
        MacroDefinition definition,
        SessionMacroTransformKind kind,
        ExactWheelRecordingTarget destination,
        Func<ExactWheelRecording> transform) =>
        GetOrTransform(
            definition,
            kind,
            destination,
            transform,
            static (callback, _) => callback());

    internal ExactWheelRecording GetOrTransform<TState>(
        MacroDefinition definition,
        SessionMacroTransformKind kind,
        ExactWheelRecordingTarget destination,
        TState state,
        Func<TState, ExactWheelRecordingTarget, ExactWheelRecording> transform)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(transform);
        var key = new TransformCacheKey(
            MacroCacheKey.Create(definition),
            kind,
            DestinationCacheKey.Create(destination));
        if (_transformed.TryGetValue(key, out var cached))
            return cached.Recording;

        var recording = transform(state, destination) ??
            throw new InvalidDataException("The macro transform returned no recording.");
        TryCacheTransform(key, recording);
        return recording;
    }

    internal ExactWheelRecording GetOrLoadAndTransform<TLoaderState>(
        MacroDefinition definition,
        SessionMacroTransformKind kind,
        ExactWheelRecordingTarget destination,
        TLoaderState loaderState,
        Func<TLoaderState, MacroDefinition, ExactWheelRecording> loader,
        Func<ExactWheelRecording, ExactWheelRecordingTarget,
            ExactWheelRecording> transform)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(transform);
        var key = new TransformCacheKey(
            MacroCacheKey.Create(definition),
            kind,
            DestinationCacheKey.Create(destination));
        if (_transformed.TryGetValue(key, out var cached))
            return cached.Recording;

        // Resolve the source only after the transformed lookup misses. The
        // transformed working set is intentionally larger than the source
        // cache, so doing this in the opposite order can re-read a large
        // uncached source on every otherwise-cached client cycle.
        var source = GetOrLoad(definition, loaderState, loader);
        var recording = transform(source, destination) ??
            throw new InvalidDataException(
                "The macro transform returned no recording.");
        TryCacheTransform(key, recording);
        return recording;
    }

    internal int CachedSourceCount => _sources.Count;

    internal int CachedTransformedCount => _transformed.Count;

    private void TryCacheSource(
        MacroCacheKey key,
        ExactWheelRecording recording)
    {
        var eventCount = recording.Events.Count;
        if (_sources.Count >= MaximumSourceEntries ||
            eventCount > MaximumSourceEvents - _sourceEventCount)
        {
            return;
        }

        _sources.Add(key, recording);
        _sourceEventCount += eventCount;
    }

    private void TryCacheTransform(
        TransformCacheKey key,
        ExactWheelRecording recording)
    {
        var eventCount = recording.Events.Count;
        if (eventCount > MaximumEventsPerTransformedEntry)
            return;

        RemoveStaleTransformForSameTarget(key);
        if (_transformed.Count >= MaximumTransformedEntries ||
            eventCount > MaximumTransformedEvents - _transformedEventCount)
        {
            // A repeating client scan must not evict the entry needed next and
            // degrade into zero cache hits. Keep the admitted working set and
            // let only this over-budget transform run one-shot.
            return;
        }

        _transformed.Add(
            key,
            new TransformedCacheEntry(recording, eventCount));
        _transformedEventCount += eventCount;
    }

    private void RemoveStaleTransformForSameTarget(TransformCacheKey key)
    {
        TransformCacheKey? staleKey = null;
        foreach (var existing in _transformed.Keys)
        {
            if (existing.Macro == key.Macro &&
                existing.Kind == key.Kind &&
                existing.Destination.WindowHandle ==
                    key.Destination.WindowHandle)
            {
                staleKey = existing;
                break;
            }
        }
        if (staleKey is not { } stale)
            return;
        if (_transformed.Remove(stale, out var removed))
            _transformedEventCount -= removed.EventCount;
    }

    private readonly record struct MacroCacheKey(
        string ContentId,
        string Sha256,
        string SafeFileName,
        SessionMacroKind Kind)
    {
        internal static MacroCacheKey Create(MacroDefinition definition) =>
            new(
                definition.ContentId,
                definition.Sha256,
                definition.SafeFileName,
                definition.Kind);
    }

    private readonly record struct TransformCacheKey(
        MacroCacheKey Macro,
        SessionMacroTransformKind Kind,
        DestinationCacheKey Destination);

    private readonly record struct DestinationCacheKey(
        long WindowHandle,
        ExactWheelDisplayTopology Display,
        string ProcessBasename,
        string WindowClass,
        ExactWheelRect WindowRect,
        ExactWheelRect ClientRect)
    {
        internal static DestinationCacheKey Create(
            ExactWheelRecordingTarget destination)
        {
            var metadata = destination.Metadata;
            return new DestinationCacheKey(
                destination.WindowHandle.ToInt64(),
                destination.Display,
                metadata.ProcessBasename,
                metadata.WindowClass,
                metadata.WindowRect,
                metadata.ClientRect);
        }
    }

    private sealed class TransformCacheKeyComparer :
        IEqualityComparer<TransformCacheKey>
    {
        internal static TransformCacheKeyComparer Instance { get; } = new();

        public bool Equals(TransformCacheKey left, TransformCacheKey right) =>
            left.Macro == right.Macro &&
            left.Kind == right.Kind &&
            DestinationsEqual(left.Destination, right.Destination);

        public int GetHashCode(TransformCacheKey key)
        {
            var destination = key.Destination;
            var display = destination.Display;
            var hash = new HashCode();
            hash.Add(key.Macro);
            hash.Add(key.Kind);
            hash.Add(destination.WindowHandle);
            hash.Add(display.VirtualLeft);
            hash.Add(display.VirtualTop);
            hash.Add(display.VirtualWidth);
            hash.Add(display.VirtualHeight);
            hash.Add(display.Monitors.Count);
            for (var index = 0; index < display.Monitors.Count; index++)
            {
                var monitor = display.Monitors[index];
                hash.Add(monitor.Bounds);
                hash.Add(monitor.DpiX);
                hash.Add(monitor.DpiY);
            }
            hash.Add(destination.ProcessBasename, StringComparer.Ordinal);
            hash.Add(destination.WindowClass, StringComparer.Ordinal);
            hash.Add(destination.WindowRect);
            hash.Add(destination.ClientRect);
            return hash.ToHashCode();
        }

        private static bool DestinationsEqual(
            DestinationCacheKey left,
            DestinationCacheKey right)
        {
            if (left.WindowHandle != right.WindowHandle ||
                !string.Equals(
                    left.ProcessBasename,
                    right.ProcessBasename,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    left.WindowClass,
                    right.WindowClass,
                    StringComparison.Ordinal) ||
                left.WindowRect != right.WindowRect ||
                left.ClientRect != right.ClientRect)
            {
                return false;
            }

            var leftDisplay = left.Display;
            var rightDisplay = right.Display;
            if (ReferenceEquals(leftDisplay, rightDisplay))
                return true;
            if (leftDisplay.VirtualLeft != rightDisplay.VirtualLeft ||
                leftDisplay.VirtualTop != rightDisplay.VirtualTop ||
                leftDisplay.VirtualWidth != rightDisplay.VirtualWidth ||
                leftDisplay.VirtualHeight != rightDisplay.VirtualHeight ||
                leftDisplay.Monitors.Count != rightDisplay.Monitors.Count)
            {
                return false;
            }

            for (var index = 0; index < leftDisplay.Monitors.Count; index++)
            {
                if (leftDisplay.Monitors[index] !=
                    rightDisplay.Monitors[index])
                {
                    return false;
                }
            }
            return true;
        }
    }

    private sealed record TransformedCacheEntry(
        ExactWheelRecording Recording,
        int EventCount);
}
