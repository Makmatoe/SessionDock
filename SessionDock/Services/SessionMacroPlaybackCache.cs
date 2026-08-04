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
/// Keeps verified source macros and small coordinate plans in memory for one
/// playback run. Source events remain memory-bounded, while destination plans
/// contain only immutable geometry and therefore scale linearly with active
/// assignments instead of copying every event for every client.
/// </summary>
internal sealed class SessionMacroPlaybackCache : IDisposable
{
    // Keep the common mixed-mode case (one maximum-size per-client macro and
    // one maximum-size whole-layout macro) warm. This is still a fixed memory
    // ceiling independent of n. Additional unique recordings move once into
    // immutable page-backed storage instead of being deserialized every loop
    // or multiplying resident event arrays per destination.
    internal const int MaximumSourceEvents =
        checked((int)ExactWheelLimits.MaximumEventCount * 2);
    internal const int MaximumSourceArtifacts =
        SessionTemplatePolicy.MaximumSlotsPerTemplate + 1;
    // Do not let a syntactically valid 128-source template consume gigabytes
    // of Temp space or provoke that much real-time antivirus I/O. Runs with a
    // larger unique working set fail preflight before creating the next map.
    internal const long MaximumPageableSourceBytes =
        256L * 1024L * 1024L;
    internal static readonly TimeSpan DisplayRefreshInterval =
        TimeSpan.FromSeconds(2);

    private const int PageableEventBytes = 40;

    private readonly Dictionary<ArtifactCacheKey, SourceCacheEntry> _sources =
        [];
    private readonly Dictionary<TransformIdentityKey, CoordinateTransformEntry>
        _coordinateTransforms = [];
    private readonly Func<ExactWheelRecording, int> _getSourceEventWeight;
    private readonly long _maximumPageableSourceBytes;
    private int _residentSourceEventCount;
    private long _pageableSourceBytes;
    private int _pageableSourceCount;
    private bool _disposed;
    private ExactWheelDisplayTopology? _displayTopology;
    private long _lastDisplayTimestamp = -1;
    private long _nextDisplayRefreshTimestamp;

    internal SessionMacroPlaybackCache()
        : this(
            static recording => recording.Events.Count,
            MaximumPageableSourceBytes)
    {
    }

    // The weight seam lets scaling tests model maximum-size sources without
    // allocating millions of events. Production always uses the exact count.
    internal SessionMacroPlaybackCache(
        Func<ExactWheelRecording, int> getSourceEventWeight,
        long maximumPageableSourceBytes = MaximumPageableSourceBytes)
    {
        _getSourceEventWeight = getSourceEventWeight ??
            throw new ArgumentNullException(nameof(getSourceEventWeight));
        if (maximumPageableSourceBytes is < 0 or >
            MaximumPageableSourceBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPageableSourceBytes));
        }
        _maximumPageableSourceBytes = maximumPageableSourceBytes;
    }

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
        ThrowIfDisposed();
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

    internal ExactWheelRecording GetOrLoadCancellable<TState>(
        MacroDefinition definition,
        TState state,
        Func<TState, MacroDefinition, ExactWheelRecording> loader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(loader);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var key = ArtifactCacheKey.Create(definition);
        if (_sources.TryGetValue(key, out var cached))
            return cached.Recording;
        EnsureSourceAdmission(definition);

        var recording = loader(state, definition) ??
            throw new InvalidDataException(
                "The macro loader returned no recording.");
        cancellationToken.ThrowIfCancellationRequested();
        return CacheSource(key, recording, cancellationToken);
    }

    internal ExactWheelRecording GetOrLoad<TState>(
        MacroDefinition definition,
        TState state,
        Func<TState, MacroDefinition, ExactWheelRecording> loader)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(loader);
        ThrowIfDisposed();
        var key = ArtifactCacheKey.Create(definition);
        if (_sources.TryGetValue(key, out var cached))
            return cached.Recording;
        EnsureSourceAdmission(definition);

        var recording = loader(state, definition) ??
            throw new InvalidDataException("The macro loader returned no recording.");
        return CacheSource(key, recording, CancellationToken.None);
    }

    private void EnsureSourceAdmission(MacroDefinition definition)
    {
        if (_sources.Count >= MaximumSourceArtifacts)
        {
            throw new InvalidDataException(
                $"One playback run supports at most {MaximumSourceArtifacts} unique macro sources.");
        }
        if (definition.EventCount is > 0 and <=
                (int)ExactWheelLimits.MaximumEventCount &&
            definition.EventCount >
                MaximumSourceEvents - _residentSourceEventCount)
        {
            var declaredPageableBytes = checked(
                (long)definition.EventCount * PageableEventBytes);
            if (declaredPageableBytes >
                _maximumPageableSourceBytes - _pageableSourceBytes)
            {
                throw new InvalidDataException(
                    "The pageable macro source budget was exceeded.");
            }
        }
    }

    internal SessionMacroPlaybackPlan GetOrLoadAndCreateTransform<
        TLoaderState>(
        MacroDefinition definition,
        SessionMacroTransformKind kind,
        ExactWheelRecordingTarget destination,
        TLoaderState loaderState,
        Func<TLoaderState, MacroDefinition, ExactWheelRecording> loader,
        Func<ExactWheelRecording, ExactWheelRecordingTarget,
            ExactWheelPlaybackCoordinateTransform> createTransform)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(createTransform);
        var artifactKey = ArtifactCacheKey.Create(definition);
        var transformKey = new TransformIdentityKey(
            artifactKey,
            kind,
            destination.WindowHandle.ToInt64());
        var destinationKey = DestinationCacheKey.Create(destination);
        var source = GetOrLoad(definition, loaderState, loader);
        if (_coordinateTransforms.TryGetValue(
                transformKey,
                out var cached) &&
            DestinationsEqual(cached.Destination, destinationKey))
        {
            return new SessionMacroPlaybackPlan(source, cached.Transform);
        }

        var transform = createTransform(source, destination) ??
            throw new InvalidDataException(
                "The macro coordinate transform factory returned no plan.");
        _coordinateTransforms[transformKey] = new CoordinateTransformEntry(
            destinationKey,
            transform);
        return new SessionMacroPlaybackPlan(source, transform);
    }

    internal int CachedSourceCount => _sources.Count;

    internal int CachedResidentSourceEventCount =>
        _residentSourceEventCount;

    internal int CachedPageableSourceCount => _pageableSourceCount;

    internal long CachedPageableSourceBytes => _pageableSourceBytes;

    internal IReadOnlyList<string> PageableSourcePaths => _sources.Values
        .Select(source => source.PageableOwner?.BackingPath)
        .Where(path => path is not null)
        .Select(path => path!)
        .ToArray();

    internal int CachedCoordinateTransformCount =>
        _coordinateTransforms.Count;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var source in _sources.Values)
            source.PageableOwner?.Dispose();
        _sources.Clear();
        _coordinateTransforms.Clear();
        _displayTopology = null;
        _residentSourceEventCount = 0;
        _pageableSourceCount = 0;
        _pageableSourceBytes = 0;
    }

    private ExactWheelRecording CacheSource(
        ArtifactCacheKey key,
        ExactWheelRecording recording,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var eventWeight = _getSourceEventWeight(recording);
        if (eventWeight is < 0 or >
            (int)ExactWheelLimits.MaximumEventCount)
        {
            throw new InvalidDataException(
                "The macro source event weight is outside the safety limit.");
        }

        if (eventWeight <=
            MaximumSourceEvents - _residentSourceEventCount)
        {
            _sources.Add(key, new SourceCacheEntry(recording, null));
            _residentSourceEventCount += eventWeight;
            return recording;
        }

        var pageableBytes = checked(
            (long)recording.Events.Count * PageableEventBytes);
        if (pageableBytes >
            _maximumPageableSourceBytes - _pageableSourceBytes)
        {
            throw new InvalidDataException(
                "The pageable macro source budget was exceeded.");
        }

        var pageable = ExactWheelPageableRecording.CreateCancellable(
            recording,
            cancellationToken);
        try
        {
            _sources.Add(
                key,
                new SourceCacheEntry(pageable.Recording, pageable));
            _pageableSourceCount++;
            _pageableSourceBytes += pageableBytes;
            return pageable.Recording;
        }
        catch
        {
            pageable.Dispose();
            throw;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct ArtifactCacheKey(
        string Sha256,
        string SafeFileName)
    {
        internal static ArtifactCacheKey Create(MacroDefinition definition) =>
            new(
                definition.Sha256,
                definition.SafeFileName);
    }

    private readonly record struct TransformIdentityKey(
        ArtifactCacheKey Artifact,
        SessionMacroTransformKind Kind,
        long WindowHandle);

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
            if (leftDisplay.Monitors[index] != rightDisplay.Monitors[index])
                return false;
        }
        return true;
    }

    private sealed record CoordinateTransformEntry(
        DestinationCacheKey Destination,
        ExactWheelPlaybackCoordinateTransform Transform);

    private sealed record SourceCacheEntry(
        ExactWheelRecording Recording,
        ExactWheelPageableRecording? PageableOwner);
}

internal readonly record struct SessionMacroPlaybackPlan(
    ExactWheelRecording Recording,
    ExactWheelPlaybackCoordinateTransform CoordinateTransform);

/// <summary>
/// Transfers one preflight cache into the launched macro context without
/// leaking it when launch, cancellation, or window discovery fails first.
/// </summary>
internal sealed class SessionMacroPlaybackCacheReservation : IDisposable
{
    private SessionMacroPlaybackCache? _cache;

    internal SessionMacroPlaybackCacheReservation(
        SessionMacroPlaybackCache? cache = null)
    {
        _cache = cache ?? new SessionMacroPlaybackCache();
    }

    internal SessionMacroPlaybackCache Cache =>
        Volatile.Read(ref _cache) ??
        throw new ObjectDisposedException(
            nameof(SessionMacroPlaybackCacheReservation));

    internal SessionMacroPlaybackCache? Take() =>
        Interlocked.Exchange(ref _cache, null);

    internal static void ReleaseFailedTransfer(
        ref SessionMacroPlaybackCache? publishedCache,
        SessionMacroPlaybackCache transferredCache,
        bool wasPublished)
    {
        ArgumentNullException.ThrowIfNull(transferredCache);
        if (wasPublished &&
            !ReferenceEquals(
                Interlocked.CompareExchange(
                    ref publishedCache,
                    null,
                    transferredCache),
                transferredCache))
        {
            // Another owner already consumed or replaced the exact cache.
            return;
        }

        transferredCache.Dispose();
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _cache, null)?.Dispose();
}
