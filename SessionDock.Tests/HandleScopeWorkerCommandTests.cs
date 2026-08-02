using System.Text;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeWorkerCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.HandleScopeWorker.{Guid.NewGuid():N}");

    [Fact]
    public void Parser_AcceptsOnlyExactPrivateInvocation()
    {
        var arguments = new[]
        {
            HandleScopeWorkerCommand.CommandName,
            "42",
            "133900000000000000"
        };

        Assert.True(HandleScopeWorkerCommand.IsInvocation(arguments));
        Assert.True(HandleScopeWorkerCommand.TryParse(arguments, out var parsed));
        Assert.Equal(42, parsed!.ParentProcessId);
        Assert.Equal(133900000000000000, parsed.ParentStartTimeFileTimeUtc);

        Assert.False(HandleScopeWorkerCommand.TryParse(
            [HandleScopeWorkerCommand.CommandName, "42"],
            out _));
        Assert.False(HandleScopeWorkerCommand.TryParse(
            [HandleScopeWorkerCommand.CommandName, "0", "1"],
            out _));
        Assert.False(HandleScopeWorkerCommand.TryParse(
            [HandleScopeWorkerCommand.CommandName, "42", "1", "extra"],
            out _));
        Assert.False(HandleScopeWorkerCommand.IsInvocation(
            ["--sessiondock-handlescope-worker", "42", "1"]));
    }

    [Fact]
    public async Task StartSignal_AcceptsOnlyTheExactClosedInput()
    {
        await using var exact = new MemoryStream("START\n"u8.ToArray());
        await using var trailing = new MemoryStream("START\nX"u8.ToArray());
        await using var partial = new MemoryStream("STAR"u8.ToArray());

        Assert.True(await HandleScopeWorkerCommand.ReadExactStartSignalAsync(
            exact,
            TestContext.Current.CancellationToken));
        Assert.False(await HandleScopeWorkerCommand.ReadExactStartSignalAsync(
            trailing,
            TestContext.Current.CancellationToken));
        Assert.False(await HandleScopeWorkerCommand.ReadExactStartSignalAsync(
            partial,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartSignal_TrailingReadHonorsItsDeadline()
    {
        await using var stream = new BlockingTrailingStream("START\n"u8.ToArray());
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HandleScopeWorkerCommand.ReadExactStartSignalAsync(
                stream,
                cancellation.Token));
    }

    [Fact]
    public void ParentSnapshot_RequiresTheExactDistinctCreatorPid()
    {
        var expected = new WindowsProcessParentSnapshot(
            ProcessId: 400,
            ParentProcessId: 200);

        Assert.True(WindowsProcessParentVerifier.MatchesExpectedCreator(
            currentProcessId: 400,
            expectedParentProcessId: 200,
            expected));
        Assert.False(WindowsProcessParentVerifier.MatchesExpectedCreator(
            currentProcessId: 401,
            expectedParentProcessId: 200,
            expected));
        Assert.False(WindowsProcessParentVerifier.MatchesExpectedCreator(
            currentProcessId: 400,
            expectedParentProcessId: 201,
            expected));
        Assert.False(WindowsProcessParentVerifier.MatchesExpectedCreator(
            currentProcessId: 400,
            expectedParentProcessId: 400,
            new WindowsProcessParentSnapshot(400, 400)));
        Assert.InRange(
            WindowsProcessParentVerifier.MaximumSnapshotEntries,
            1,
            100_000);
    }

    [Fact]
    public void RuntimeSourceStore_RoundTripsStrictSchema()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "handlescope-runtime.json");
        var store = new HandleScopeRuntimeSourceStore(path);

        var missing = store.Read();
        Assert.False(missing.Exists);
        Assert.True(missing.IsValid);
        Assert.Equal(HandleScopeRuntimeSource.Bundled, missing.Source);

        store.Write(HandleScopeRuntimeSource.Standalone);
        var standalone = store.Read();
        Assert.True(standalone.Exists);
        Assert.True(standalone.IsValid);
        Assert.Equal(HandleScopeRuntimeSource.Standalone, standalone.Source);

        var json = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"runtimeSource\": \"standalone\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSourceStore_PreservesInvalidInput()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "handlescope-runtime.json");
        const string invalid =
            "{\"schemaVersion\":1,\"runtimeSource\":\"bundled\",\"extra\":true}";
        File.WriteAllText(path, invalid, new UTF8Encoding(false));

        var result = new HandleScopeRuntimeSourceStore(path).Read();

        Assert.True(result.Exists);
        Assert.False(result.IsValid);
        Assert.Equal(invalid, File.ReadAllText(path, Encoding.UTF8));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class BlockingTrailingStream(byte[] prefix) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return ValueTask.FromResult(count);
            }

            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
