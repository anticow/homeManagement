namespace HomeManagement.Abstractions.Models;

// ── Process listing ──

/// <summary>
/// Snapshot of a running process on a managed endpoint.
/// Returned by <see cref="Interfaces.IProcessListService.ListAsync"/>.
/// </summary>
public record ProcessInfo(
    int ProcessId,
    string ProcessName,
    long WorkingSetBytes,
    ProcessStatus Status);

public enum ProcessStatus
{
    Running,
    Sleeping,
    Stopped,
    Unknown
}
