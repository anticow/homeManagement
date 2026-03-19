using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Abstracts remote command execution across SSH, WinRM, PowerShell Remoting, and Agent transports.
/// </summary>
public interface IRemoteExecutor
{
    Task<RemoteResult> ExecuteAsync(MachineTarget target, RemoteCommand command, CancellationToken ct = default);

    Task TransferFileAsync(
        MachineTarget target,
        FileTransferRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Tests connectivity and detects OS information. Returns a rich result instead of a boolean.
    /// </summary>
    Task<ConnectionTestResult> TestConnectionAsync(MachineTarget target, CancellationToken ct = default);
}

/// <summary>
/// Resolves remote file paths according to the target machine's OS conventions.
/// </summary>
public interface IRemotePathResolver
{
    string NormalizePath(string path, OsType targetOs);
    string CombinePath(OsType targetOs, params string[] segments);
    char GetSeparator(OsType targetOs);
}
