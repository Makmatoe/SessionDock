using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SessionDock.HandleScope;
using SessionDock.Services;

namespace SessionDock.SystemProcesses;

internal sealed record HandleScopeWorkerInvocation(
    int ParentProcessId,
    long ParentStartTimeFileTimeUtc);

internal static class HandleScopeWorkerCommand
{
    internal const string CommandName =
        "--sessiondock-internal-handlescope-worker";
    internal static readonly TimeSpan StartSignalTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly byte[] StartSignal = "START\n"u8.ToArray();

    internal static bool IsInvocation(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 &&
        string.Equals(arguments[0], CommandName, StringComparison.Ordinal);

    internal static bool TryParse(
        IReadOnlyList<string> arguments,
        out HandleScopeWorkerInvocation? invocation)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        invocation = null;
        if (arguments.Count != 3 ||
            !string.Equals(arguments[0], CommandName, StringComparison.Ordinal) ||
            !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentProcessId) ||
            parentProcessId <= 0 ||
            !long.TryParse(
                arguments[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentStartTimeFileTimeUtc) ||
            parentStartTimeFileTimeUtc <= 0)
        {
            return false;
        }

        invocation = new(
            parentProcessId,
            parentStartTimeFileTimeUtc);
        return true;
    }

    internal static int Run(IReadOnlyList<string> arguments)
    {
        if (!TryParse(arguments, out var invocation) || invocation is null)
            return 2;

        try
        {
            return RunAsync(invocation).GetAwaiter().GetResult();
        }
        catch
        {
            return 5;
        }
    }

    private static async Task<int> RunAsync(
        HandleScopeWorkerInvocation invocation)
    {
        using var parent = Process.GetProcessById(invocation.ParentProcessId);
        using var current = Process.GetCurrentProcess();
        var expectedExecutablePath = Environment.ProcessPath;
        var parentExecutablePath = parent.MainModule?.FileName;
        if (expectedExecutablePath is null ||
            parentExecutablePath is null ||
            parent.HasExited ||
            parent.Id == current.Id ||
            parent.StartTime.ToUniversalTime().ToFileTimeUtc() !=
                invocation.ParentStartTimeFileTimeUtc ||
            !Path.GetFullPath(parentExecutablePath).Equals(
                Path.GetFullPath(expectedExecutablePath),
                StringComparison.OrdinalIgnoreCase) ||
            !WindowsProcessParentVerifier.IsCurrentProcessCreatedBy(parent.Id) ||
            !WindowsProcessSecurity.IsOwnedStandardUserProcessInCurrentSession(
                parent) ||
            !RuntimeSecurityPolicy.IsCurrentProcessSupported(out _))
        {
            return 4;
        }

        using var parentExited = new CancellationTokenSource();
        parent.EnableRaisingEvents = true;
        parent.Exited += Parent_Exited;
        if (parent.HasExited)
            parentExited.Cancel();

        try
        {
            await using var input = Console.OpenStandardInput();
            await using var output = Console.OpenStandardOutput();
            using var startDeadline =
                CancellationTokenSource.CreateLinkedTokenSource(
                    parentExited.Token);
            startDeadline.CancelAfter(StartSignalTimeout);
            try
            {
                if (!await ReadExactStartSignalAsync(
                        input,
                        startDeadline.Token).ConfigureAwait(false))
                {
                    return 4;
                }
            }
            catch (OperationCanceledException) when (
                startDeadline.IsCancellationRequested)
            {
                return 4;
            }

            var broker = new HandleScopeBroker();
            await broker.RunAsync(output, parentExited.Token)
                .ConfigureAwait(false);
            return parentExited.IsCancellationRequested ? 0 : 1;
        }
        finally
        {
            parent.Exited -= Parent_Exited;
        }

        void Parent_Exited(object? sender, EventArgs e) => parentExited.Cancel();
    }

    internal static async Task<bool> ReadExactStartSignalAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var signal = new byte[StartSignal.Length];
        var offset = 0;
        while (offset < signal.Length)
        {
            var read = await input.ReadAsync(
                signal.AsMemory(offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return false;
            offset += read;
        }

        var trailing = new byte[1];
        var trailingRead = await input.ReadAsync(
            trailing,
            cancellationToken).ConfigureAwait(false);
        return trailingRead == 0 && signal.AsSpan().SequenceEqual(StartSignal);
    }
}
