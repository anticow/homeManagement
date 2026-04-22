using System.Diagnostics;

namespace HomeManagement.Automation;

/// <summary>
/// Abstraction over process execution to enable deterministic testing of timeout and cancellation behavior.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Starts a process and waits for it to exit or be cancelled.
    /// </summary>
    /// <param name="fileName">The executable to run (e.g., "pwsh").</param>
    /// <param name="arguments">Command-line arguments to pass to the executable.</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="ct">Cancellation token; when cancelled, the process should be terminated.</param>
    /// <returns>Process result containing exit code and output streams.</returns>
    Task<ProcessResult> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken ct);
}

/// <summary>
/// Result of process execution.
/// </summary>
public sealed record ProcessResult(
    int? ExitCode,
    string StdOut,
    string StdErr,
    bool WasCancelled);
