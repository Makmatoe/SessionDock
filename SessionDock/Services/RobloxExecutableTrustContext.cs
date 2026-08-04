using System.IO;
using Microsoft.Win32.SafeHandles;

namespace SessionDock.Services;

/// <summary>
/// Coordinates executable trust refreshes within one bounded operation. A
/// process that fails before Authenticode is reached does not consume the
/// forced proof. Once a path is actually trusted, later processes at that
/// exact path can skip duplicate file hashing and Authenticode work. A
/// rejected proof is also shared only for this operation, so an untrusted
/// executable fails closed once instead of launching n identical trust walks.
/// </summary>
internal sealed class RobloxExecutableTrustContext : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<string, SafeFileHandle?> _retainExecutable;
    private readonly Dictionary<string, VerificationEntry> _entries = new(
        StringComparer.Ordinal);
    private bool _disposed;

    internal RobloxExecutableTrustContext()
        : this(RetainExecutable)
    {
    }

    internal RobloxExecutableTrustContext(
        Func<string, SafeFileHandle?> retainExecutable)
    {
        _retainExecutable = retainExecutable ??
            throw new ArgumentNullException(nameof(retainExecutable));
    }

    internal VerificationClaim AcquireVerification(
        string? executablePath,
        CancellationToken cancellationToken = default)
    {
        if (!TryCanonicalize(executablePath, out var canonicalPath))
            return VerificationClaim.UncoordinatedForced;

        var entry = GetOrAddEntry(canonicalPath);
        var completed = entry.CompletedState;
        if (completed != ExecutableTrustState.Unknown)
            return VerificationClaim.FromCompletedState(entry, completed);
        entry.Gate.Wait(cancellationToken);
        completed = entry.CompletedState;
        if (completed != ExecutableTrustState.Unknown)
        {
            entry.Gate.Release();
            return VerificationClaim.FromCompletedState(entry, completed);
        }

        entry.EnsureExecutableRetained();
        return new VerificationClaim(entry);
    }

    internal ValueTask<VerificationClaim> AcquireVerificationAsync(
        string? executablePath,
        CancellationToken cancellationToken = default)
    {
        if (!TryCanonicalize(executablePath, out var canonicalPath))
        {
            return ValueTask.FromResult(
                VerificationClaim.UncoordinatedForced);
        }

        return AcquireEntryAsync(
            GetOrAddEntry(canonicalPath),
            cancellationToken);
    }

    private VerificationEntry GetOrAddEntry(string canonicalPath)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(canonicalPath, out var entry))
            {
                entry = new VerificationEntry(
                    canonicalPath,
                    _retainExecutable);
                _entries.Add(canonicalPath, entry);
            }

            return entry;
        }
    }

    private static async ValueTask<VerificationClaim> AcquireEntryAsync(
        VerificationEntry entry,
        CancellationToken cancellationToken)
    {
        var completed = entry.CompletedState;
        if (completed != ExecutableTrustState.Unknown)
            return VerificationClaim.FromCompletedState(entry, completed);

        await entry.Gate.WaitAsync(cancellationToken);
        completed = entry.CompletedState;
        if (completed != ExecutableTrustState.Unknown)
        {
            entry.Gate.Release();
            return VerificationClaim.FromCompletedState(entry, completed);
        }

        entry.EnsureExecutableRetained();
        return new VerificationClaim(entry);
    }

    private static bool TryCanonicalize(
        string? executablePath,
        out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        try
        {
            canonicalPath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            // Never turn an unnormalizable identity into a cache hit. The
            // underlying verifier receives a forced request and rejects the
            // malformed or unavailable executable through its normal path.
            return false;
        }

        return true;
    }

    internal sealed class VerificationEntry
    {
        private readonly string _canonicalPath;
        private readonly Func<string, SafeFileHandle?> _retainExecutable;
        private int _completedState;
        private bool _retainAttempted;
        private SafeFileHandle? _executableRetention;

        internal VerificationEntry(
            string canonicalPath,
            Func<string, SafeFileHandle?> retainExecutable)
        {
            _canonicalPath = canonicalPath;
            _retainExecutable = retainExecutable;
        }

        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal ExecutableTrustState CompletedState =>
            (ExecutableTrustState)Volatile.Read(ref _completedState);

        internal bool CanShareResult => _executableRetention is not null;

        internal SafeFileHandle? ExecutableHandle => _executableRetention;

        internal void EnsureExecutableRetained()
        {
            if (_retainAttempted)
                return;
            _retainAttempted = true;
            try
            {
                var retention = _retainExecutable(_canonicalPath);
                if (retention is null ||
                    retention.IsInvalid ||
                    retention.IsClosed)
                {
                    retention?.Dispose();
                    return;
                }

                _executableRetention = retention;
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or
                    NotSupportedException or UnauthorizedAccessException or
                    System.Security.SecurityException)
            {
                _executableRetention = null;
            }
        }

        internal void Complete(ExecutableTrustState state)
        {
            if (state == ExecutableTrustState.Unknown || !CanShareResult)
                return;
            Volatile.Write(ref _completedState, (int)state);
        }

        internal void Dispose()
        {
            _executableRetention?.Dispose();
            _executableRetention = null;
            Gate.Dispose();
        }
    }

    internal sealed class VerificationClaim : IDisposable
    {
        internal static VerificationClaim Rejected { get; } = new(
            forceTrustRefresh: false,
            verifyExecutableTrust: false,
            executableTrustRejected: true);
        internal static VerificationClaim UncoordinatedForced { get; } = new(
            forceTrustRefresh: true,
            verifyExecutableTrust: true,
            executableTrustRejected: false);

        private VerificationEntry? _entry;

        private VerificationClaim(
            bool forceTrustRefresh,
            bool verifyExecutableTrust,
            bool executableTrustRejected,
            SafeFileHandle? executableHandle = null)
        {
            ForceTrustRefresh = forceTrustRefresh;
            VerifyExecutableTrust = verifyExecutableTrust;
            ExecutableTrustRejected = executableTrustRejected;
            ExecutableHandle = executableHandle;
        }

        internal VerificationClaim(VerificationEntry entry)
        {
            _entry = entry;
            ForceTrustRefresh = true;
            VerifyExecutableTrust = true;
            ExecutableHandle = entry.ExecutableHandle;
        }

        internal bool ForceTrustRefresh { get; }

        internal bool VerifyExecutableTrust { get; }

        internal bool ExecutableTrustRejected { get; }

        internal SafeFileHandle? ExecutableHandle { get; }

        internal void ReportVerification(
            RobloxProcessVerificationStatus status)
        {
            var entry = _entry;
            if (entry is null)
                return;

            entry.Complete(status switch
            {
                RobloxProcessVerificationStatus.Verified or
                    RobloxProcessVerificationStatus.WrongUserOrSession =>
                    ExecutableTrustState.Trusted,
                RobloxProcessVerificationStatus.ExecutableNotTrusted =>
                    ExecutableTrustState.Rejected,
                _ => ExecutableTrustState.Unknown
            });
        }

        internal static VerificationClaim FromCompletedState(
            VerificationEntry entry,
            ExecutableTrustState state) => state switch
            {
                ExecutableTrustState.Trusted => new VerificationClaim(
                    forceTrustRefresh: false,
                    verifyExecutableTrust: false,
                    executableTrustRejected: false,
                    entry.ExecutableHandle),
                ExecutableTrustState.Rejected => Rejected,
                _ => throw new ArgumentOutOfRangeException(nameof(state))
            };

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null)
                return;

            entry.Gate.Release();
        }
    }

    internal enum ExecutableTrustState
    {
        Unknown,
        Trusted,
        Rejected
    }

    public void Dispose()
    {
        VerificationEntry[] entries;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = [.. _entries.Values];
            _entries.Clear();
        }

        foreach (var entry in entries)
            entry.Dispose();
    }

    private static SafeFileHandle? RetainExecutable(string canonicalPath)
    {
        var handle = File.OpenHandle(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }
}
