using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Returns a snapshot of running processes from a managed endpoint.
/// The implementation delegates to the agent running on the target machine
/// via <see cref="IRemoteExecutor"/> and the "ProcessList" command type.
/// </summary>
public interface IProcessListService
{
    /// <summary>
    /// Returns the list of running processes on <paramref name="target"/>.
    /// Returns an empty list when the remote agent is unreachable or the command fails.
    /// </summary>
    Task<IReadOnlyList<ProcessInfo>> ListAsync(MachineTarget target, CancellationToken ct = default);
}
