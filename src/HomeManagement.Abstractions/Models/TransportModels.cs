namespace HomeManagement.Abstractions.Models;

// ── Remote Execution ──

public record RemoteCommand(
    string CommandText,
    TimeSpan Timeout,
    ElevationMode Elevation = ElevationMode.None,
    string? RunAsUser = null,
    IDictionary<string, string>? EnvironmentVariables = null);

public record RemoteResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut);

public record FileTransferRequest(
    string LocalPath,
    string RemotePath,
    FileTransferDirection Direction,
    byte[]? ExpectedSha256 = null,
    bool OverwriteIfExists = true);

public record TransferProgress(
    long BytesTransferred,
    long TotalBytes,
    double PercentComplete);

/// <summary>
/// Result of a connection test — richer than a simple boolean.
/// </summary>
public record ConnectionTestResult(
    bool Reachable,
    OsType? DetectedOs,
    string? OsVersion,
    TimeSpan Latency,
    string? ProtocolVersion,
    string? ErrorMessage);
