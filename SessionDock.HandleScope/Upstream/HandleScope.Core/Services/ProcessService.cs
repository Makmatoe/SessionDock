using System.Diagnostics;
using HandleScope.Models;

namespace HandleScope.Services;

public sealed class ProcessService
{
    private readonly ProcessIdentityService _identityService;

    public ProcessService()
        : this(new ProcessIdentityService())
    {
    }

    public ProcessService(ProcessIdentityService identityService)
    {
        ArgumentNullException.ThrowIfNull(identityService);
        _identityService = identityService;
    }

    public IReadOnlyList<ProcessRow> GetProcesses()
    {
        return GetProcessSnapshots()
            .Select(snapshot => snapshot.Row)
            .ToArray();
    }

    public IReadOnlyList<ProcessSnapshot> GetProcessSnapshots()
    {
        var snapshots = new List<ProcessSnapshot>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var identity = _identityService.GetIdentity(process.Id);
                    int? handleCount = null;
                    long? workingSet = null;

                    try
                    {
                        handleCount = process.HandleCount;
                    }
                    catch
                    {
                        // A protected or exiting process may deny individual properties.
                    }

                    try
                    {
                        workingSet = process.WorkingSet64;
                    }
                    catch
                    {
                        // Keep the process visible even when memory data is unavailable.
                    }

                    var row = new ProcessRow(
                        identity.ProcessId,
                        identity.ProcessName,
                        handleCount,
                        workingSet)
                    {
                        ProcessCreationTimeUtcFileTime =
                            identity.CreationTimeUtcFileTime
                    };
                    snapshots.Add(new ProcessSnapshot(row, identity));
                }
                catch
                {
                    // Exiting or protected processes cannot provide a pinned identity.
                }
            }
        }

        return snapshots
            .OrderBy(snapshot => snapshot.Row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snapshot => snapshot.Row.ProcessId)
            .ToArray();
    }
}
