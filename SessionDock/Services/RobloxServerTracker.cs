using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SessionDock.Services;

public sealed class RobloxServerTracker
{
    internal const int MaximumCandidateLogs = 128;
    internal const int MaximumActiveQueryKeys = 128;
    internal const int MaximumScanReadBytes = 4 * 1024 * 1024;
    internal const int MaximumRetainedInactiveObservations = 1024;
    internal const int ContinuityTailBytes = 64;
    private const int MaximumLogTailBytes = 512 * 1024;
    private const int MaximumLogLineCharacters = 16 * 1024;
    private const int MaximumLogLineBytes = MaximumLogLineCharacters * 4;
    private static readonly TimeSpan TrackingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TrackingPollInterval =
        TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LogTimestampTolerance = TimeSpan.FromSeconds(5);
    private static readonly Regex JoinPattern = new(
        @"Joining game '(?<job>[0-9a-fA-F-]{36})' place (?<place>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UserPattern = new(
        @"\buserid:(?<user>\d+),",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TimestampPattern = new(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _captureSync = new();
    private readonly string _logDirectory;
    private readonly RobloxServerTrackingScanCoordinator _scanCoordinator;
    private readonly Dictionary<string, LogScanState> _logStates = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RobloxServerObservationKey, RetainedObservation>
        _latestObservations = [];
    private readonly Dictionary<RobloxServerObservationKey, int>
        _activeQueryRefCounts = [];
    private readonly HashSet<RobloxServerObservationKey>
        _pendingBackfillKeys = [];
    private readonly LinkedList<RobloxServerObservationKey>
        _inactiveObservationOrder = [];
    private long _totalBytesRead;
    private long _totalLogOpenCount;
    private long _observationGeneration;
    private long _snapshotGeneration = -1;
    private int _lastScanBytesRead;
    private int _lastScanLogOpenCount;
    private RobloxServerTrackingSnapshot _cachedObservationSnapshot =
        RobloxServerTrackingSnapshot.Empty;

    public RobloxServerTracker()
        : this(Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "logs"))
    {
    }

    internal RobloxServerTracker(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _logDirectory = logDirectory;
        _scanCoordinator = new RobloxServerTrackingScanCoordinator(
            CaptureSnapshot);
    }

    internal int LastScanBytesRead => Volatile.Read(ref _lastScanBytesRead);

    internal long TotalBytesRead => Interlocked.Read(ref _totalBytesRead);

    internal int LastScanLogOpenCount =>
        Volatile.Read(ref _lastScanLogOpenCount);

    internal long TotalLogOpenCount =>
        Interlocked.Read(ref _totalLogOpenCount);

    internal int ActiveQueryCount
    {
        get
        {
            lock (_captureSync)
                return _activeQueryRefCounts.Count;
        }
    }

    internal int RetainedObservationCount
    {
        get
        {
            lock (_captureSync)
                return _latestObservations.Count;
        }
    }

    internal int TrackedLogCount
    {
        get
        {
            lock (_captureSync)
                return _logStates.Count;
        }
    }

    public async Task<string?> FindJoinedServerAsync(
        long expectedUserId,
        long expectedPlaceId,
        DateTimeOffset launchStartedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedUserId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedPlaceId);
        cancellationToken.ThrowIfCancellationRequested();

        using var queryRegistration = TryRegisterActiveQuery(
            new RobloxServerObservationKey(expectedUserId, expectedPlaceId));
        if (queryRegistration is null)
            return null;

        var deadline = DateTimeOffset.UtcNow + TrackingTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await _scanCoordinator.GetSnapshotAsync(
                cancellationToken);
            var serverJobId = FindJoinedServer(
                snapshot,
                expectedUserId,
                expectedPlaceId,
                launchStartedAt);
            if (serverJobId is not null)
                return serverJobId;

            await Task.Delay(TrackingPollInterval, cancellationToken);
        }

        return null;
    }

    private IDisposable? TryRegisterActiveQuery(
        RobloxServerObservationKey key)
    {
        lock (_captureSync)
        {
            if (_activeQueryRefCounts.TryGetValue(key, out var count))
            {
                _activeQueryRefCounts[key] = checked(count + 1);
            }
            else
            {
                if (_activeQueryRefCounts.Count >= MaximumActiveQueryKeys)
                    return null;

                _activeQueryRefCounts.Add(key, 1);
                if (_latestObservations.TryGetValue(key, out var retained))
                {
                    RemoveFromInactiveOrder(retained);
                }
                else
                {
                    _pendingBackfillKeys.Add(key);
                }
            }
        }

        return new ActiveQueryRegistration(this, key);
    }

    private void UnregisterActiveQuery(RobloxServerObservationKey key)
    {
        lock (_captureSync)
        {
            if (!_activeQueryRefCounts.TryGetValue(key, out var count))
                return;

            if (count > 1)
            {
                _activeQueryRefCounts[key] = count - 1;
                return;
            }

            _activeQueryRefCounts.Remove(key);
            _pendingBackfillKeys.Remove(key);
            if (_latestObservations.TryGetValue(key, out var retained) &&
                retained.InactiveNode is null)
            {
                AddToInactiveOrder(key, retained);
            }
            TrimInactiveObservations();
        }
    }

    private static string? FindJoinedServer(
        RobloxServerTrackingSnapshot snapshot,
        long expectedUserId,
        long expectedPlaceId,
        DateTimeOffset launchStartedAt)
    {
        var earliestTimestamp = launchStartedAt - LogTimestampTolerance;
        return snapshot.FindJoinedServer(
            expectedUserId,
            expectedPlaceId,
            earliestTimestamp);
    }

    internal RobloxServerTrackingSnapshot CaptureSnapshot()
    {
        lock (_captureSync)
            return CaptureSnapshotCore();
    }

    private RobloxServerTrackingSnapshot CaptureSnapshotCore()
    {
        Volatile.Write(ref _lastScanBytesRead, 0);
        Volatile.Write(ref _lastScanLogOpenCount, 0);
        try
        {
            if (!Directory.Exists(_logDirectory) ||
                IsReparsePoint(_logDirectory))
            {
                return RobloxServerTrackingSnapshot.Empty;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return RobloxServerTrackingSnapshot.Empty;
        }

        FileInfo[] candidates;
        var capturedAt = DateTimeOffset.UtcNow;
        try
        {
            var earliestCandidateWrite = capturedAt.UtcDateTime -
                TrackingTimeout -
                LogTimestampTolerance;
            candidates = new DirectoryInfo(_logDirectory)
                .EnumerateFiles("*_Player_*.log", SearchOption.TopDirectoryOnly)
                .Where(file => !IsReparsePoint(file.FullName))
                .Where(file => file.LastWriteTimeUtc >= earliestCandidateWrite)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaximumCandidateLogs)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return RobloxServerTrackingSnapshot.Empty;
        }

        RemoveUnselectedLogStates(candidates);
        if (candidates.Length == 0)
            return GetObservationSnapshot();

        var attemptedBackfillKeys = _pendingBackfillKeys.ToArray();
        var forceBackfill = attemptedBackfillKeys.Length > 0;
        var perFileReadBudget = Math.Min(
            MaximumLogTailBytes,
            MaximumScanReadBytes / candidates.Length);
        var readBuffer = ArrayPool<byte>.Shared.Rent(
            Math.Min(perFileReadBudget, 64 * 1024));
        var scanBytesRead = 0;
        var scanLogOpenCount = 0;
        try
        {
            foreach (var file in candidates)
            {
                if (!_logStates.TryGetValue(file.FullName, out var state))
                {
                    state = new LogScanState();
                    _logStates.Add(file.FullName, state);
                }

                if (!TryReadMetadata(file, out var metadata))
                {
                    if (forceBackfill || state.NeedsBackfill)
                        state.NeedsBackfill = true;
                    continue;
                }

                var forceStateBackfill = forceBackfill || state.NeedsBackfill;
                if (!forceStateBackfill && CanSkipLogOpen(state, metadata))
                    continue;

                scanLogOpenCount++;
                var result = ReadLogIncrementally(
                    file,
                    state,
                    metadata,
                    perFileReadBudget,
                    readBuffer,
                    forceStateBackfill);
                scanBytesRead += result.BytesRead;
                state.NeedsBackfill = forceStateBackfill && !result.Completed;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            Volatile.Write(ref _lastScanBytesRead, scanBytesRead);
            Volatile.Write(ref _lastScanLogOpenCount, scanLogOpenCount);
            Interlocked.Add(ref _totalBytesRead, scanBytesRead);
            Interlocked.Add(ref _totalLogOpenCount, scanLogOpenCount);
        }

        foreach (var key in attemptedBackfillKeys)
            _pendingBackfillKeys.Remove(key);

        var earliestObservation = capturedAt -
            TrackingTimeout -
            LogTimestampTolerance;
        RemoveObservationsBefore(earliestObservation);
        foreach (var state in _logStates.Values)
        {
            if (state.PendingTimestamp < earliestObservation)
                state.ClearPendingJoin();
        }

        return GetObservationSnapshot();
    }

    private void RemoveUnselectedLogStates(IReadOnlyList<FileInfo> candidates)
    {
        var selectedPaths = candidates
            .Select(file => file.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _logStates.Keys
            .Where(path => !selectedPaths.Contains(path))
            .ToArray())
        {
            InvalidateState(_logStates[path]);
            _logStates.Remove(path);
        }
    }

    private static bool TryReadMetadata(
        FileInfo file,
        out LogFileMetadata metadata)
    {
        try
        {
            file.Refresh();
            metadata = new LogFileMetadata(
                file.Length,
                file.CreationTimeUtc,
                file.LastWriteTimeUtc);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            metadata = default;
            return false;
        }
    }

    private static bool CanSkipLogOpen(
        LogScanState state,
        LogFileMetadata metadata) =>
        !state.NeedsBackfill &&
        state.Initialized &&
        state.CreationTimeUtc == metadata.CreationTimeUtc &&
        state.LastWriteTimeUtc == metadata.LastWriteTimeUtc &&
        state.LastObservedLength == metadata.Length &&
        state.NextOffset >= metadata.Length;

    private LogReadResult ReadLogIncrementally(
        FileInfo file,
        LogScanState state,
        LogFileMetadata metadata,
        int byteBudget,
        byte[] readBuffer,
        bool forceBackfill)
    {
        var totalBytesRead = 0;
        var completed = false;
        try
        {
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var invalidate = ShouldInvalidateState(
                state,
                length,
                metadata.CreationTimeUtc,
                metadata.LastWriteTimeUtc);
            var metadataChanged = HasMetadataChanged(
                state,
                length,
                metadata.CreationTimeUtc,
                metadata.LastWriteTimeUtc);
            if (!invalidate && metadataChanged && state.ContinuityLength > 0)
            {
                invalidate = !ValidateContinuity(
                    stream,
                    state,
                    byteBudget,
                    readBuffer,
                    ref totalBytesRead);
            }

            var remainingBudget = byteBudget - totalBytesRead;
            if (invalidate)
            {
                InvalidateState(state);
                InitializeColdState(state, length, remainingBudget);
            }
            else if (forceBackfill)
            {
                state.Reset();
                InitializeColdState(state, length, remainingBudget);
            }

            state.CreationTimeUtc = metadata.CreationTimeUtc;
            state.LastWriteTimeUtc = metadata.LastWriteTimeUtc;
            state.LastObservedLength = length;
            stream.Position = state.NextOffset;

            var remaining = checked((int)Math.Min(
                remainingBudget,
                length - state.NextOffset));
            while (remaining > 0)
            {
                var requested = Math.Min(remaining, readBuffer.Length);
                var read = stream.Read(readBuffer, 0, requested);
                if (read == 0)
                    break;

                var bytes = readBuffer.AsSpan(0, read);
                ProcessBytes(state, bytes);
                state.AppendContinuity(bytes);
                state.NextOffset += read;
                totalBytesRead += read;
                remaining -= read;
            }

            // StringReader processed the final unterminated log line in the
            // original scanner. Inspect it without clearing the buffer so a
            // later append can still finish the same line incrementally.
            if (state.NextOffset == length &&
                !state.DiscardUntilNewline &&
                !state.DiscardingLongLine &&
                state.PendingLine.WrittenCount > 0)
            {
                ProcessLine(state);
            }

            completed = true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Roblox can rotate or replace a log while it is being inspected.
        }

        return new LogReadResult(totalBytesRead, completed);
    }

    private static bool ValidateContinuity(
        FileStream stream,
        LogScanState state,
        int byteBudget,
        byte[] readBuffer,
        ref int totalBytesRead)
    {
        var continuityLength = state.ContinuityLength;
        if (continuityLength == 0)
            return true;
        if (continuityLength > byteBudget ||
            state.NextOffset < continuityLength)
        {
            return false;
        }

        stream.Position = state.NextOffset - continuityLength;
        var readTotal = 0;
        while (readTotal < continuityLength)
        {
            var read = stream.Read(
                readBuffer,
                readTotal,
                continuityLength - readTotal);
            if (read == 0)
                break;
            readTotal += read;
            totalBytesRead += read;
        }

        return readTotal == continuityLength &&
            readBuffer.AsSpan(0, continuityLength)
                .SequenceEqual(state.ContinuitySpan);
    }

    private static bool ShouldInvalidateState(
        LogScanState state,
        long length,
        DateTime creationTimeUtc,
        DateTime lastWriteTimeUtc) =>
        !state.Initialized ||
        state.CreationTimeUtc != creationTimeUtc ||
        length < state.NextOffset ||
        (length <= state.LastObservedLength &&
            state.LastWriteTimeUtc != lastWriteTimeUtc);

    private static bool HasMetadataChanged(
        LogScanState state,
        long length,
        DateTime creationTimeUtc,
        DateTime lastWriteTimeUtc) =>
        state.CreationTimeUtc != creationTimeUtc ||
        state.LastWriteTimeUtc != lastWriteTimeUtc ||
        state.LastObservedLength != length;

    private static void InitializeColdState(
        LogScanState state,
        long length,
        int byteBudget)
    {
        state.Initialized = true;
        state.NextOffset = Math.Max(0, length - byteBudget);
        state.DiscardUntilNewline = state.NextOffset > 0;
    }

    private void ProcessBytes(
        LogScanState state,
        ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (state.DiscardUntilNewline)
            {
                if (value == (byte)'\n')
                    state.DiscardUntilNewline = false;
                continue;
            }

            if (state.DiscardingLongLine)
            {
                if (value == (byte)'\n')
                {
                    state.DiscardingLongLine = false;
                    state.ClearPendingJoin();
                }
                continue;
            }

            if (value == (byte)'\n')
            {
                ProcessLine(state);
                state.PendingLine.Clear();
                continue;
            }

            if (state.PendingLine.WrittenCount == MaximumLogLineBytes)
            {
                state.PendingLine.Clear();
                state.DiscardingLongLine = true;
                continue;
            }

            var destination = state.PendingLine.GetSpan(1);
            destination[0] = value;
            state.PendingLine.Advance(1);
        }
    }

    private void ProcessLine(LogScanState state)
    {
        var bytes = state.PendingLine.WrittenSpan;
        if (!bytes.IsEmpty && bytes[^1] == (byte)'\r')
            bytes = bytes[..^1];

        var line = Encoding.UTF8.GetString(bytes);
        if (line.Length > 0 && line[0] == '\uFEFF')
            line = line[1..];
        if (line.Length > MaximumLogLineCharacters)
        {
            state.ClearPendingJoin();
            return;
        }

        var joinMatch = JoinPattern.Match(line);
        if (joinMatch.Success &&
            TryReadTimestamp(line, out var timestamp) &&
            long.TryParse(
                joinMatch.Groups["place"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var placeId) &&
            Guid.TryParse(joinMatch.Groups["job"].Value, out var jobId))
        {
            state.PendingServerJobId = jobId.ToString("D");
            state.PendingPlaceId = placeId;
            state.PendingTimestamp = timestamp;
            return;
        }

        if (state.PendingServerJobId is null)
            return;

        var userMatch = UserPattern.Match(line);
        if (!userMatch.Success ||
            !long.TryParse(
                userMatch.Groups["user"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var userId))
        {
            return;
        }

        var observation = new RobloxServerObservation(
            state.PendingTimestamp,
            state.PendingPlaceId,
            userId,
            state.PendingServerJobId);
        var key = new RobloxServerObservationKey(userId, state.PendingPlaceId);
        RetainObservation(state, key, observation);
    }

    private void RetainObservation(
        LogScanState sourceState,
        RobloxServerObservationKey key,
        RobloxServerObservation observation)
    {
        if (_latestObservations.TryGetValue(key, out var retained))
        {
            if (observation.Timestamp < retained.Observation.Timestamp)
                return;

            if (observation != retained.Observation)
                _observationGeneration++;
            retained.Observation = observation;
            retained.SourceState = sourceState;
            RemoveFromInactiveOrder(retained);
        }
        else
        {
            retained = new RetainedObservation(observation, sourceState);
            _latestObservations.Add(key, retained);
            _observationGeneration++;
        }

        if (_activeQueryRefCounts.ContainsKey(key))
        {
            _pendingBackfillKeys.Remove(key);
        }
        else
        {
            AddToInactiveOrder(key, retained);
            TrimInactiveObservations();
        }
    }

    private void AddToInactiveOrder(
        RobloxServerObservationKey key,
        RetainedObservation retained)
    {
        retained.InactiveNode = _inactiveObservationOrder.AddLast(key);
    }

    private void RemoveFromInactiveOrder(RetainedObservation retained)
    {
        if (retained.InactiveNode is not { } node)
            return;

        _inactiveObservationOrder.Remove(node);
        retained.InactiveNode = null;
    }

    private void TrimInactiveObservations()
    {
        while (_inactiveObservationOrder.Count >
            MaximumRetainedInactiveObservations)
        {
            var node = _inactiveObservationOrder.First!;
            _inactiveObservationOrder.RemoveFirst();
            if (_latestObservations.TryGetValue(
                    node.Value,
                    out var retained) &&
                ReferenceEquals(retained.InactiveNode, node))
            {
                retained.InactiveNode = null;
                _latestObservations.Remove(node.Value);
                _observationGeneration++;
            }
        }
    }

    private void RemoveObservationsBefore(DateTimeOffset earliestTimestamp)
    {
        foreach (var key in _latestObservations
            .Where(pair =>
                pair.Value.Observation.Timestamp < earliestTimestamp)
            .Select(pair => pair.Key)
            .ToArray())
        {
            RemoveObservation(key);
        }
    }

    private void RemoveObservation(RobloxServerObservationKey key)
    {
        if (!_latestObservations.Remove(key, out var retained))
            return;

        RemoveFromInactiveOrder(retained);
        _observationGeneration++;
    }

    private RobloxServerTrackingSnapshot GetObservationSnapshot()
    {
        if (_snapshotGeneration == _observationGeneration)
            return _cachedObservationSnapshot;

        _cachedObservationSnapshot = _latestObservations.Count == 0
            ? RobloxServerTrackingSnapshot.Empty
            : new RobloxServerTrackingSnapshot(
                _latestObservations.Values
                    .Select(retained => retained.Observation)
                    .ToArray());
        _snapshotGeneration = _observationGeneration;
        return _cachedObservationSnapshot;
    }

    private void InvalidateState(LogScanState state)
    {
        foreach (var key in _latestObservations
            .Where(pair => ReferenceEquals(pair.Value.SourceState, state))
            .Select(pair => pair.Key)
            .ToArray())
        {
            var active = _activeQueryRefCounts.ContainsKey(key);
            RemoveObservation(key);
            if (active)
                _pendingBackfillKeys.Add(key);
        }
        state.Reset();
    }

    private static bool TryReadTimestamp(
        string line,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        var match = TimestampPattern.Match(line);
        return match.Success && DateTimeOffset.TryParseExact(
            match.Groups["timestamp"].Value,
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private sealed class LogScanState
    {
        private readonly byte[] _continuityTail = new byte[ContinuityTailBytes];

        internal ArrayBufferWriter<byte> PendingLine { get; } = new(256);

        internal bool Initialized { get; set; }

        internal long NextOffset { get; set; }

        internal long LastObservedLength { get; set; }

        internal DateTime CreationTimeUtc { get; set; }

        internal DateTime LastWriteTimeUtc { get; set; }

        internal bool DiscardUntilNewline { get; set; }

        internal bool DiscardingLongLine { get; set; }

        internal bool NeedsBackfill { get; set; }

        internal string? PendingServerJobId { get; set; }

        internal long PendingPlaceId { get; set; }

        internal DateTimeOffset PendingTimestamp { get; set; }

        internal int ContinuityLength { get; private set; }

        internal ReadOnlySpan<byte> ContinuitySpan =>
            _continuityTail.AsSpan(0, ContinuityLength);

        internal void AppendContinuity(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= ContinuityTailBytes)
            {
                bytes[^ContinuityTailBytes..].CopyTo(_continuityTail);
                ContinuityLength = ContinuityTailBytes;
                return;
            }

            var retainedLength = Math.Min(
                ContinuityLength,
                ContinuityTailBytes - bytes.Length);
            _continuityTail.AsSpan(
                    ContinuityLength - retainedLength,
                    retainedLength)
                .CopyTo(_continuityTail);
            bytes.CopyTo(_continuityTail.AsSpan(retainedLength));
            ContinuityLength = retainedLength + bytes.Length;
        }

        internal void ClearPendingJoin()
        {
            PendingServerJobId = null;
            PendingPlaceId = 0;
            PendingTimestamp = default;
        }

        internal void Reset()
        {
            Initialized = false;
            NextOffset = 0;
            LastObservedLength = 0;
            CreationTimeUtc = default;
            LastWriteTimeUtc = default;
            DiscardUntilNewline = false;
            DiscardingLongLine = false;
            NeedsBackfill = false;
            PendingLine.Clear();
            ClearPendingJoin();
            ContinuityLength = 0;
        }
    }

    private sealed class RetainedObservation(
        RobloxServerObservation observation,
        LogScanState sourceState)
    {
        internal RobloxServerObservation Observation { get; set; } =
            observation;

        internal LogScanState SourceState { get; set; } = sourceState;

        internal LinkedListNode<RobloxServerObservationKey>? InactiveNode
        { get; set; }
    }

    private sealed class ActiveQueryRegistration(
        RobloxServerTracker tracker,
        RobloxServerObservationKey key) : IDisposable
    {
        private RobloxServerTracker? _tracker = tracker;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _tracker, null);
            owner?.UnregisterActiveQuery(key);
        }
    }

    private readonly record struct LogFileMetadata(
        long Length,
        DateTime CreationTimeUtc,
        DateTime LastWriteTimeUtc);

    private readonly record struct LogReadResult(
        int BytesRead,
        bool Completed);

    private readonly record struct RobloxServerObservationKey(
        long UserId,
        long PlaceId);
}
