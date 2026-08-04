using System.Globalization;
using System.Text;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class RobloxServerTrackingScanCoordinatorTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(8, 87.5)]
    [InlineData(32, 96.875)]
    [InlineData(100, 99)]
    [InlineData(128, 99.21875)]
    public async Task ConcurrentClients_ShareOneLogScanPerPollingWindow(
        int clientCount,
        double expectedScanReductionPercent)
    {
        long timestamp = 0;
        var captureCount = 0;
        using var captureStarted = new ManualResetEventSlim();
        using var releaseCapture = new ManualResetEventSlim();
        var expected = new RobloxServerTrackingSnapshot(
        [
            new RobloxServerObservation(
                DateTimeOffset.UnixEpoch,
                1,
                2,
                Guid.Empty.ToString("D"))
        ]);
        var coordinator = new RobloxServerTrackingScanCoordinator(
            () =>
            {
                Interlocked.Increment(ref captureCount);
                captureStarted.Set();
                releaseCapture.Wait(TestContext.Current.CancellationToken);
                return expected;
            },
            () => timestamp,
            started => TimeSpan.FromMilliseconds(timestamp - started));

        var requests = Enumerable.Range(0, clientCount)
            .Select(_ => coordinator.GetSnapshotAsync(
                TestContext.Current.CancellationToken))
            .ToArray();
        Assert.True(captureStarted.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref captureCount));
        releaseCapture.Set();

        var snapshots = await Task.WhenAll(requests);
        Assert.All(snapshots, snapshot => Assert.Same(expected, snapshot));
        var reductionPercent =
            (clientCount - Volatile.Read(ref captureCount)) * 100d /
            clientCount;
        Assert.Equal(
            expectedScanReductionPercent,
            reductionPercent,
            precision: 5);

        timestamp = 499;
        Assert.Same(
            expected,
            await coordinator.GetSnapshotAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref captureCount));

        timestamp = 500;
        Assert.Same(
            expected,
            await coordinator.GetSnapshotAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(2, Volatile.Read(ref captureCount));
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelTheSharedScan()
    {
        using var captureStarted = new ManualResetEventSlim();
        using var releaseCapture = new ManualResetEventSlim();
        var coordinator = new RobloxServerTrackingScanCoordinator(
            () =>
            {
                captureStarted.Set();
                releaseCapture.Wait(TestContext.Current.CancellationToken);
                return RobloxServerTrackingSnapshot.Empty;
            });
        using var cancellation = new CancellationTokenSource();

        var cancelled = coordinator.GetSnapshotAsync(cancellation.Token);
        Assert.True(captureStarted.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        var survivor = coordinator.GetSnapshotAsync(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelled);
        releaseCapture.Set();

        Assert.Same(
            RobloxServerTrackingSnapshot.Empty,
            await survivor);
    }

    [Fact]
    public async Task Tracker_SharedSnapshotPreservesUserPlaceAndTimeMatching()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "SessionDock.ServerTracking.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var joinedAt = DateTimeOffset.UtcNow;
            var serverJobId = Guid.NewGuid();
            var logTimestamp = joinedAt.UtcDateTime.ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture);
            var logPath = Path.Combine(
                temporaryRoot,
                "test_Player_001.log");
            await File.WriteAllLinesAsync(
                logPath,
                [
                    $"{logTimestamp} Joining game '{serverJobId:D}' place 456",
                    $"{logTimestamp} userid:123, connected"
                ],
                TestContext.Current.CancellationToken);
            var tracker = new RobloxServerTracker(temporaryRoot);
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(2));

            var actual = await tracker.FindJoinedServerAsync(
                expectedUserId: 123,
                expectedPlaceId: 456,
                launchStartedAt: joinedAt - TimeSpan.FromSeconds(1),
                timeout.Token);

            Assert.Equal(serverJobId.ToString("D"), actual);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Tracker_ColdScanCovers128LogsWithinFixedBudget()
    {
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var timestamp = FormatTimestamp(observedAt);
            var expectedJobs = new Guid[RobloxServerTracker.MaximumCandidateLogs];
            var leadingPartialLine = new string('x', 40 * 1024) + '\n';
            for (var index = 0; index < expectedJobs.Length; index++)
            {
                expectedJobs[index] = Guid.NewGuid();
                File.WriteAllText(
                    Path.Combine(
                        temporaryRoot,
                        $"scan_Player_{index:D3}.log"),
                    leadingPartialLine +
                    $"{timestamp} Joining game '{expectedJobs[index]:D}' " +
                    $"place {20_000 + index}\n" +
                    $"{timestamp} userid:{10_000 + index}, connected\n");
            }

            var tracker = new RobloxServerTracker(temporaryRoot);
            var snapshot = tracker.CaptureSnapshot();

            Assert.Equal(
                RobloxServerTracker.MaximumCandidateLogs,
                tracker.TrackedLogCount);
            Assert.Equal(
                RobloxServerTracker.MaximumScanReadBytes,
                tracker.LastScanBytesRead);
            Assert.Equal(
                RobloxServerTracker.MaximumCandidateLogs,
                tracker.LastScanLogOpenCount);
            for (var index = 0; index < expectedJobs.Length; index++)
            {
                Assert.Equal(
                    expectedJobs[index].ToString("D"),
                    snapshot.FindJoinedServer(
                        expectedUserId: 10_000 + index,
                        expectedPlaceId: 20_000 + index,
                        earliestTimestamp: observedAt - TimeSpan.FromSeconds(1)));
            }

            var totalBytesRead = tracker.TotalBytesRead;
            var totalLogOpenCount = tracker.TotalLogOpenCount;
            var unchangedSnapshot = tracker.CaptureSnapshot();

            Assert.Equal(0, tracker.LastScanBytesRead);
            Assert.Equal(0, tracker.LastScanLogOpenCount);
            Assert.Equal(totalBytesRead, tracker.TotalBytesRead);
            Assert.Equal(totalLogOpenCount, tracker.TotalLogOpenCount);
            Assert.Same(snapshot, unchangedSnapshot);
            Assert.Equal(
                expectedJobs[0].ToString("D"),
                unchangedSnapshot.FindJoinedServer(
                    expectedUserId: 10_000,
                    expectedPlaceId: 20_000,
                    earliestTimestamp: observedAt - TimeSpan.FromSeconds(1)));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Tracker_IncrementalAppendReadsOnlyNewBytesAndKeepsParserState()
    {
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var timestamp = FormatTimestamp(observedAt);
            var serverJobId = Guid.NewGuid();
            var logPath = Path.Combine(
                temporaryRoot,
                "append_Player_001.log");
            var initialText =
                $"{timestamp} Joining game '{serverJobId:D}' place 456";
            File.WriteAllText(logPath, initialText);
            var tracker = new RobloxServerTracker(temporaryRoot);

            var beforeUserLine = tracker.CaptureSnapshot();
            Assert.Null(beforeUserLine.FindJoinedServer(
                expectedUserId: 123,
                expectedPlaceId: 456,
                earliestTimestamp: observedAt - TimeSpan.FromSeconds(1)));
            Assert.Equal(
                Encoding.UTF8.GetByteCount(initialText),
                tracker.LastScanBytesRead);

            var bytesAfterInitialScan = tracker.TotalBytesRead;
            _ = tracker.CaptureSnapshot();
            Assert.Equal(0, tracker.LastScanBytesRead);
            Assert.Equal(bytesAfterInitialScan, tracker.TotalBytesRead);

            var appendedText = $"\n{timestamp} userid:123, connected\n";
            File.AppendAllText(logPath, appendedText);
            var afterAppend = tracker.CaptureSnapshot();
            var continuityBytes = Math.Min(
                Encoding.UTF8.GetByteCount(initialText),
                RobloxServerTracker.ContinuityTailBytes);
            var incrementalBytesRead =
                continuityBytes + Encoding.UTF8.GetByteCount(appendedText);

            Assert.Equal(
                incrementalBytesRead,
                tracker.LastScanBytesRead);
            Assert.Equal(
                bytesAfterInitialScan + incrementalBytesRead,
                tracker.TotalBytesRead);
            Assert.Equal(
                serverJobId.ToString("D"),
                afterAppend.FindJoinedServer(
                    expectedUserId: 123,
                    expectedPlaceId: 456,
                    earliestTimestamp: observedAt - TimeSpan.FromSeconds(1)));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Tracker_TargetSurvives512LaterUserIdLines()
    {
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var timestamp = FormatTimestamp(observedAt);
            var serverJobId = Guid.NewGuid();
            var contents = new StringBuilder()
                .Append(timestamp)
                .Append(" Joining game '")
                .Append(serverJobId.ToString("D"))
                .Append("' place 456\n")
                .Append(timestamp)
                .Append(" userid:123, connected\n");
            for (var index = 0; index < 512; index++)
            {
                _ = contents
                    .Append(timestamp)
                    .Append(" userid:")
                    .Append(10_000 + index)
                    .Append(", noise\n");
            }

            File.WriteAllText(
                Path.Combine(temporaryRoot, "noise_Player_001.log"),
                contents.ToString());
            var tracker = new RobloxServerTracker(temporaryRoot);

            var snapshot = tracker.CaptureSnapshot();

            Assert.Equal(
                serverJobId.ToString("D"),
                snapshot.FindJoinedServer(
                    expectedUserId: 123,
                    expectedPlaceId: 456,
                    earliestTimestamp: observedAt - TimeSpan.FromSeconds(1)));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Tracker_NoisyLogCannotEvictAnActiveQueryOrGrowUnbounded()
    {
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var timestamp = FormatTimestamp(observedAt);
            var serverJobId = Guid.NewGuid();
            var contents = new StringBuilder()
                .Append(timestamp)
                .Append(" Joining game '")
                .Append(serverJobId.ToString("D"))
                .Append("' place 456\n")
                .Append(timestamp)
                .Append(" userid:123, connected\n");
            var noisyObservationCount =
                RobloxServerTracker.MaximumRetainedInactiveObservations * 4;
            for (var index = 0; index < noisyObservationCount; index++)
            {
                _ = contents
                    .Append(timestamp)
                    .Append(" userid:")
                    .Append(100_000 + index)
                    .Append(", noise\n");
            }

            File.WriteAllText(
                Path.Combine(temporaryRoot, "bounded_Player_001.log"),
                contents.ToString());
            var tracker = new RobloxServerTracker(temporaryRoot);
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(2));

            var actual = await tracker.FindJoinedServerAsync(
                expectedUserId: 123,
                expectedPlaceId: 456,
                launchStartedAt: observedAt - TimeSpan.FromSeconds(1),
                timeout.Token);

            Assert.Equal(serverJobId.ToString("D"), actual);
            Assert.InRange(
                tracker.RetainedObservationCount,
                0,
                RobloxServerTracker.MaximumRetainedInactiveObservations);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Tracker_TruncateAndRegrowPastOldLengthResetsFromContinuity()
    {
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var timestamp = FormatTimestamp(observedAt);
            var oldServerJobId = Guid.NewGuid();
            var newServerJobId = Guid.NewGuid();
            var logPath = Path.Combine(
                temporaryRoot,
                "rewrite_Player_001.log");
            var oldText =
                $"{timestamp} Joining game '{oldServerJobId:D}' place 456\n" +
                $"{timestamp} userid:123, connected\n";
            File.WriteAllText(logPath, oldText);
            var creationTimeUtc = File.GetCreationTimeUtc(logPath);
            var tracker = new RobloxServerTracker(temporaryRoot);

            var beforeRewrite = tracker.CaptureSnapshot();
            Assert.Equal(
                oldServerJobId.ToString("D"),
                beforeRewrite.FindJoinedServer(
                    expectedUserId: 123,
                    expectedPlaceId: 456,
                    earliestTimestamp: observedAt - TimeSpan.FromSeconds(1)));

            var newText =
                new string('r', oldText.Length + 32) + "\n" +
                $"{timestamp} Joining game '{newServerJobId:D}' place 456\n" +
                $"{timestamp} userid:123, connected\n";
            File.WriteAllText(logPath, newText);
            File.SetCreationTimeUtc(logPath, creationTimeUtc);
            Assert.True(newText.Length > oldText.Length);

            var afterRewrite = tracker.CaptureSnapshot();

            Assert.Equal(
                newServerJobId.ToString("D"),
                afterRewrite.FindJoinedServer(
                    expectedUserId: 123,
                    expectedPlaceId: 456,
                    earliestTimestamp: observedAt - TimeSpan.FromSeconds(1)));
            Assert.InRange(
                tracker.LastScanBytesRead,
                1,
                RobloxServerTracker.MaximumScanReadBytes);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Tracker_LateRegistrationBackfillsOnceAfterInactiveEviction()
    {
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var timestamp = FormatTimestamp(observedAt);
            var serverJobId = Guid.NewGuid();
            var contents = new StringBuilder()
                .Append(timestamp)
                .Append(" Joining game '")
                .Append(serverJobId.ToString("D"))
                .Append("' place 456\n")
                .Append(timestamp)
                .Append(" userid:123, connected\n");
            for (var index = 0;
                index < RobloxServerTracker
                    .MaximumRetainedInactiveObservations * 4;
                index++)
            {
                _ = contents
                    .Append(timestamp)
                    .Append(" userid:")
                    .Append(100_000 + index)
                    .Append(", noise\n");
            }

            File.WriteAllText(
                Path.Combine(temporaryRoot, "late_Player_001.log"),
                contents.ToString());
            var tracker = new RobloxServerTracker(temporaryRoot);
            var beforeRegistration = tracker.CaptureSnapshot();
            Assert.Null(beforeRegistration.FindJoinedServer(
                expectedUserId: 123,
                expectedPlaceId: 456,
                earliestTimestamp: observedAt - TimeSpan.FromSeconds(1)));
            Assert.Equal(
                RobloxServerTracker.MaximumRetainedInactiveObservations,
                tracker.RetainedObservationCount);
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(3));

            var actual = await tracker.FindJoinedServerAsync(
                expectedUserId: 123,
                expectedPlaceId: 456,
                launchStartedAt: observedAt - TimeSpan.FromSeconds(1),
                timeout.Token);

            Assert.Equal(serverJobId.ToString("D"), actual);
            Assert.Equal(1, tracker.LastScanLogOpenCount);
            Assert.InRange(
                tracker.LastScanBytesRead,
                1,
                RobloxServerTracker.MaximumScanReadBytes);

            var stableSnapshot = tracker.CaptureSnapshot();
            var stableLogOpenCount = tracker.TotalLogOpenCount;
            var unchangedSnapshot = tracker.CaptureSnapshot();

            Assert.Equal(0, tracker.LastScanLogOpenCount);
            Assert.Equal(stableLogOpenCount, tracker.TotalLogOpenCount);
            Assert.Same(stableSnapshot, unchangedSnapshot);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Tracker_FailedBackfillRetriesOnlyTheFailedLog()
    {
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            var lockedPath = Path.Combine(
                temporaryRoot,
                "locked_Player_001.log");
            File.WriteAllText(lockedPath, "locked\n");
            File.WriteAllText(
                Path.Combine(temporaryRoot, "stable_Player_002.log"),
                "stable\n");
            var tracker = new RobloxServerTracker(temporaryRoot);
            _ = tracker.CaptureSnapshot();
            var opensBeforeBackfill = tracker.TotalLogOpenCount;
            using var cancellation = new CancellationTokenSource();

            Task<string?> query;
            using (new FileStream(
                lockedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                query = tracker.FindJoinedServerAsync(
                    expectedUserId: 123,
                    expectedPlaceId: 456,
                    launchStartedAt: DateTimeOffset.UtcNow,
                    cancellation.Token);
                Assert.True(SpinWait.SpinUntil(
                    () => tracker.TotalLogOpenCount >=
                        opensBeforeBackfill + 2,
                    TimeSpan.FromSeconds(3)));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await query);
            }

            var opensBeforeTargetedRetry = tracker.TotalLogOpenCount;
            _ = tracker.CaptureSnapshot();

            Assert.Equal(1, tracker.LastScanLogOpenCount);
            Assert.Equal(
                opensBeforeTargetedRetry + 1,
                tracker.TotalLogOpenCount);

            _ = tracker.CaptureSnapshot();
            Assert.Equal(0, tracker.LastScanLogOpenCount);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Tracker_128LogsAnd128ActiveKeysUseOneGlobalBoundedIndex()
    {
        var temporaryRoot = CreateTemporaryRoot();
        var tracker = new RobloxServerTracker(temporaryRoot);
        using var cancellation = new CancellationTokenSource();
        Task<string?>[] queries = [];
        try
        {
            var observedAt = DateTimeOffset.UtcNow;
            var timestamp = FormatTimestamp(observedAt);
            const long placeId = 456;
            queries = Enumerable.Range(
                    0,
                    RobloxServerTracker.MaximumActiveQueryKeys)
                .Select(index => tracker.FindJoinedServerAsync(
                    expectedUserId: 10_000 + index,
                    expectedPlaceId: placeId,
                    launchStartedAt: observedAt + TimeSpan.FromHours(1),
                    cancellation.Token))
                .ToArray();

            Assert.True(SpinWait.SpinUntil(
                () => tracker.ActiveQueryCount ==
                    RobloxServerTracker.MaximumActiveQueryKeys,
                TimeSpan.FromSeconds(3)));
            Assert.Null(await tracker.FindJoinedServerAsync(
                expectedUserId: 999_999,
                expectedPlaceId: placeId,
                launchStartedAt: observedAt,
                cancellation.Token));
            Assert.Equal(
                RobloxServerTracker.MaximumActiveQueryKeys,
                tracker.ActiveQueryCount);

            var userLines = new StringBuilder();
            for (var index = 0;
                index < RobloxServerTracker.MaximumActiveQueryKeys;
                index++)
            {
                _ = userLines
                    .Append(timestamp)
                    .Append(" userid:")
                    .Append(10_000 + index)
                    .Append(", connected\n");
            }
            for (var index = 0;
                index < RobloxServerTracker.MaximumCandidateLogs;
                index++)
            {
                File.WriteAllText(
                    Path.Combine(
                        temporaryRoot,
                        $"global_Player_{index:D3}.log"),
                    $"{timestamp} Joining game '{Guid.NewGuid():D}' " +
                    $"place {placeId}\n{userLines}");
            }

            Assert.True(SpinWait.SpinUntil(
                () => tracker.RetainedObservationCount ==
                    RobloxServerTracker.MaximumActiveQueryKeys,
                TimeSpan.FromSeconds(5)));
            Assert.Equal(
                RobloxServerTracker.MaximumActiveQueryKeys,
                tracker.ActiveQueryCount);
            Assert.Equal(
                RobloxServerTracker.MaximumActiveQueryKeys,
                tracker.RetainedObservationCount);
            Assert.InRange(
                tracker.RetainedObservationCount,
                0,
                RobloxServerTracker.MaximumActiveQueryKeys +
                    RobloxServerTracker.MaximumRetainedInactiveObservations);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await Task.WhenAll(queries);
            }
            catch (OperationCanceledException)
            {
                // Expected: the future launch threshold keeps all keys active.
            }
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public void SnapshotIndex_FindsEachBatchClient(
        int clientCount)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var observations = Enumerable.Range(0, clientCount)
            .Select(index => new RobloxServerObservation(
                startedAt + TimeSpan.FromMilliseconds(index),
                PlaceId: 1_000 + index,
                UserId: 2_000 + index,
                ServerJobId: $"server-{index}"))
            .ToArray();
        var snapshot = new RobloxServerTrackingSnapshot(observations);

        for (var index = 0; index < clientCount; index++)
        {
            Assert.Equal(
                $"server-{index}",
                snapshot.FindJoinedServer(
                    expectedUserId: 2_000 + index,
                    expectedPlaceId: 1_000 + index,
                    earliestTimestamp: startedAt));
        }
    }

    [Fact]
    public void SnapshotIndex_ReturnsNewestEligibleObservation()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var snapshot = new RobloxServerTrackingSnapshot(
        [
            new RobloxServerObservation(
                startedAt,
                PlaceId: 456,
                UserId: 123,
                ServerJobId: "old"),
            new RobloxServerObservation(
                startedAt + TimeSpan.FromSeconds(1),
                PlaceId: 456,
                UserId: 123,
                ServerJobId: "new")
        ]);

        Assert.Equal(
            "new",
            snapshot.FindJoinedServer(
                expectedUserId: 123,
                expectedPlaceId: 456,
                earliestTimestamp: startedAt));
        Assert.Null(snapshot.FindJoinedServer(
            expectedUserId: 123,
            expectedPlaceId: 456,
            earliestTimestamp: startedAt + TimeSpan.FromSeconds(2)));
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SessionDock.ServerTracking.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
