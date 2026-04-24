using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Provides endpoint state information (service status, online/offline, hardware metrics)
/// without requiring a live remote connection. Typically backed by a metrics aggregator
/// such as Prometheus with node_exporter / windows_exporter.
///
/// All methods return <c>null</c> or safe defaults rather than throwing when data is unavailable.
/// Callers should fall back to direct remote queries when the provider returns null.
/// </summary>
public interface IEndpointStateProvider
{
    /// <summary>
    /// Returns the current state of a named service on the endpoint.
    /// Returns <see cref="ServiceState.Unknown"/> when no data is available.
    /// </summary>
    Task<ServiceState> GetServiceStateAsync(
        string hostname, string serviceName, OsType osType, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the endpoint is reachable according to the metrics collector.
    /// Returns false when no data is available — callers must decide whether to treat
    /// absence of data as offline or unknown.
    /// </summary>
    Task<bool> GetEndpointOnlineAsync(string hostname, CancellationToken ct = default);

    /// <summary>
    /// Returns hardware metrics for the endpoint.
    /// Returns null when no metrics data is available for the hostname.
    /// </summary>
    Task<HardwareMetrics?> GetHardwareMetricsAsync(
        string hostname, OsType osType, CancellationToken ct = default);
}

/// <summary>
/// Hardware resource metrics collected from a metrics exporter.
/// Used to update <see cref="Machine"/> hardware info without a direct remote command.
/// </summary>
public record HardwareMetrics(
    double? CpuUsagePercent,
    long? MemoryTotalBytes,
    long? MemoryUsedBytes,
    long? DiskTotalBytes,
    long? DiskFreeBytes,
    TimeSpan? Uptime);
