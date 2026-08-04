using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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
    internal const int MaximumTransformedEvents = 750_000;
    internal const int MaximumEventsPerTransformedEntry = 100_000;
    internal static readonly TimeSpan DisplayRefreshInterval =
        TimeSpan.FromSeconds(2);

    private readonly Dictionary<MacroCacheKey, ExactWheelRecording> _sources =
        [];
    private readonly Dictionary<TransformCacheKey, ExactWheelRecording>
        _transformed = [];
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
        Func<MacroDefinition, ExactWheelRecording> loader)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(loader);
        var key = MacroCacheKey.Create(definition);
        if (_sources.TryGetValue(key, out var cached))
            return cached;

        var recording = loader(definition) ??
            throw new InvalidDataException("The macro loader returned no recording.");
        TryCacheSource(key, recording);
        return recording;
    }

    internal ExactWheelRecording GetOrTransform(
        MacroDefinition definition,
        SessionMacroTransformKind kind,
        ExactWheelRecordingTarget destination,
        Func<ExactWheelRecording> transform)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(transform);
        var key = new TransformCacheKey(
            MacroCacheKey.Create(definition),
            kind,
            CreateDestinationIdentity(destination));
        if (_transformed.TryGetValue(key, out var cached))
            return cached;

        var recording = transform() ??
            throw new InvalidDataException("The macro transform returned no recording.");
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
        if (_transformed.Count >= MaximumTransformedEntries ||
            eventCount > MaximumEventsPerTransformedEntry ||
            eventCount > MaximumTransformedEvents - _transformedEventCount)
        {
            return;
        }

        _transformed.Add(key, recording);
        _transformedEventCount += eventCount;
    }

    private static string CreateDestinationIdentity(
        ExactWheelRecordingTarget destination)
    {
        var display = destination.Display;
        var metadata = destination.Metadata;
        var identity = new StringBuilder(256);
        AppendSigned(identity, destination.WindowHandle.ToInt64());
        AppendSigned(identity, display.VirtualLeft);
        AppendSigned(identity, display.VirtualTop);
        AppendSigned(identity, display.VirtualWidth);
        AppendSigned(identity, display.VirtualHeight);
        AppendSigned(identity, display.Monitors.Count);
        foreach (var monitor in display.Monitors)
        {
            AppendSigned(identity, monitor.Bounds.Left);
            AppendSigned(identity, monitor.Bounds.Top);
            AppendSigned(identity, monitor.Bounds.Right);
            AppendSigned(identity, monitor.Bounds.Bottom);
            AppendUnsigned(identity, monitor.DpiX);
            AppendUnsigned(identity, monitor.DpiY);
        }

        identity.Append(metadata.ProcessBasename).Append('\u001f');
        identity.Append(metadata.WindowClass).Append('\u001f');
        AppendSigned(identity, metadata.WindowRect.Left);
        AppendSigned(identity, metadata.WindowRect.Top);
        AppendSigned(identity, metadata.WindowRect.Right);
        AppendSigned(identity, metadata.WindowRect.Bottom);
        AppendSigned(identity, metadata.ClientRect.Left);
        AppendSigned(identity, metadata.ClientRect.Top);
        AppendSigned(identity, metadata.ClientRect.Right);
        AppendSigned(identity, metadata.ClientRect.Bottom);
        return identity.ToString();

        static void AppendSigned(StringBuilder builder, long value) =>
            builder.Append(value.ToString(CultureInfo.InvariantCulture))
                .Append('\u001f');

        static void AppendUnsigned(StringBuilder builder, uint value) =>
            builder.Append(value.ToString(CultureInfo.InvariantCulture))
                .Append('\u001f');
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
        string DestinationIdentity);
}
