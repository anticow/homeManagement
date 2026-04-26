using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Integration.Prometheus.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Prometheus;

/// <summary>
/// Queries Prometheus for endpoint state — service status, availability,
/// and hardware metrics. Injected into <c>ServiceControllerService</c> and
/// <c>InventoryService</c> to replace direct SSH/WinRM state reads.
///
/// All methods degrade gracefully: if Prometheus is unreachable or returns
/// no data, the methods return "Unknown" / null rather than throwing.
/// </summary>
public class PrometheusEndpointStateProvider : IEndpointStateProvider
{
    private readonly PrometheusClient _client;
    private readonly PrometheusOptions _options;
    private readonly ILogger<PrometheusEndpointStateProvider> _logger;

    public PrometheusEndpointStateProvider(
        PrometheusClient client,
        IOptions<PrometheusOptions> options,
        ILogger<PrometheusEndpointStateProvider> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    // ── Service state ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the state of a named service on the specified endpoint.
    /// Returns <see cref="ServiceState.Unknown"/> if Prometheus has no data.
    /// </summary>
    public virtual async Task<ServiceState> GetServiceStateAsync(
        string hostname, string serviceName, OsType osType, CancellationToken ct = default)
    {
        var query = osType == OsType.Windows
            ? PromQL.WindowsServiceState(hostname, serviceName, _options.ScrapeLabel)
            : PromQL.LinuxSystemdUnitState(hostname, serviceName + ".service", _options.ScrapeLabel);

        var vectors = await _client.QueryAsync(query, ct);
        if (vectors.Count == 0)
        {
            _logger.LogDebug("Prometheus: no service state data for {Service} on {Host}", serviceName, hostname);
            return ServiceState.Unknown;
        }

        return osType == OsType.Windows
            ? MapWindowsServiceState(vectors)
            : MapLinuxServiceState(vectors);
    }

    /// <summary>
    /// Returns the service state for all services on an endpoint (snapshot).
    /// Uses the full windows_service_state / node_systemd_unit_state without name filter.
    /// </summary>
    public virtual async Task<IReadOnlyList<EndpointServiceState>> GetAllServiceStatesAsync(
        string hostname, OsType osType, CancellationToken ct = default)
    {
        // Query all services (no name filter) by using a wildcard instance selector only.
        var query = osType == OsType.Windows
            ? PromQL.WindowsServiceState(hostname, "*", _options.ScrapeLabel)
              .Replace(@",name=""*""", string.Empty)  // drop name filter for all-services query
            : PromQL.LinuxSystemdUnitState(hostname, "*", _options.ScrapeLabel)
              .Replace(@",name=""*""", string.Empty);

        var vectors = await _client.QueryAsync(query, ct);
        return vectors
            .Select(v =>
            {
                var name = v.Metric.GetValueOrDefault("name", "unknown");
                var state = v.Metric.TryGetValue("state", out var stateStr) ? stateStr : "unknown";
                var domainState = osType == OsType.Windows
                    ? MapWindowsStateString(state)
                    : MapLinuxStateString(state);
                // Only report rows where metric value == 1 (that state is active)
                if (v.Value.AsDouble() is not 1.0)
                    return null;
                return new EndpointServiceState(hostname, name, domainState.ToString(), null);
            })
            .Where(s => s is not null)
            .Cast<EndpointServiceState>()
            .ToList()
            .AsReadOnly();
    }

    // ── Availability ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if Prometheus reports the endpoint as reachable (up == 1).
    /// Returns false if unreachable, or null if Prometheus has no data at all.
    /// </summary>
    public virtual async Task<EndpointAvailability> GetEndpointAvailabilityAsync(
        string hostname, CancellationToken ct = default)
    {
        var query = PromQL.EndpointUp(hostname, _options.ScrapeLabel);
        var vectors = await _client.QueryAsync(query, ct);
        var isOnline = vectors.Any(v => v.Value.AsDouble() is 1.0);

        _logger.LogDebug("Prometheus: endpoint {Host} online={Online}", hostname, isOnline);
        return new EndpointAvailability(hostname, isOnline, DateTime.UtcNow);
    }

    /// <inheritdoc/>
    public async Task<bool> GetEndpointOnlineAsync(string hostname, CancellationToken ct = default)
    {
        var availability = await GetEndpointAvailabilityAsync(hostname, ct);
        return availability.IsOnline;
    }

    /// <inheritdoc/>
    public async Task<HardwareMetrics?> GetHardwareMetricsAsync(
        string hostname, OsType osType, CancellationToken ct = default)
    {
        var metrics = await GetEndpointMetricsAsync(hostname, osType, ct);
        // If all metrics are null, Prometheus has no data for this host — return null
        // so callers know to fall back to direct remote queries.
        if (metrics.CpuUsagePercent is null && metrics.MemoryTotalBytes is null &&
            metrics.DiskTotalBytes is null)
            return null;

        return new HardwareMetrics(
            CpuUsagePercent: metrics.CpuUsagePercent,
            MemoryTotalBytes: (long?)metrics.MemoryTotalBytes,
            MemoryUsedBytes: (long?)metrics.MemoryUsedBytes,
            DiskTotalBytes: (long?)metrics.DiskTotalBytes,
            DiskFreeBytes: (long?)metrics.DiskFreeBytes,
            Uptime: metrics.Uptime);
    }

    // ── Metrics ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns resource metrics (CPU, memory, disk, uptime) for an endpoint.
    /// Any metric unavailable in Prometheus will be null in the result.
    /// </summary>
    public virtual async Task<EndpointMetrics> GetEndpointMetricsAsync(
        string hostname, OsType osType, CancellationToken ct = default)
    {
        // Issue all metric queries in parallel.
        Task<double?> cpuTask, memAvailTask, memTotalTask, diskFreeTask, diskTotalTask, uptimeTask;

        if (osType == OsType.Windows)
        {
            cpuTask = QueryScalarAsync(PromQL.WindowsCpuUsagePercent(hostname, _options.ScrapeLabel), ct);
            memAvailTask = QueryScalarAsync(PromQL.WindowsMemoryAvailableBytes(hostname, _options.ScrapeLabel), ct);
            memTotalTask = QueryScalarAsync(PromQL.WindowsMemoryPhysicalBytes(hostname, _options.ScrapeLabel), ct);
            diskFreeTask = QueryScalarAsync(PromQL.WindowsDiskFreeBytes(hostname, _options.ScrapeLabel), ct);
            diskTotalTask = QueryScalarAsync(PromQL.WindowsDiskSizeBytes(hostname, _options.ScrapeLabel), ct);
            uptimeTask = QueryScalarAsync(PromQL.WindowsSystemUptimeSeconds(hostname, _options.ScrapeLabel), ct);
        }
        else
        {
            cpuTask = QueryScalarAsync(PromQL.LinuxCpuUsagePercent(hostname, _options.ScrapeLabel), ct);
            memAvailTask = QueryScalarAsync(PromQL.LinuxMemoryAvailableBytes(hostname, _options.ScrapeLabel), ct);
            memTotalTask = QueryScalarAsync(PromQL.LinuxMemoryTotalBytes(hostname, _options.ScrapeLabel), ct);
            diskFreeTask = QueryScalarAsync(PromQL.LinuxDiskFreeBytes(hostname, _options.ScrapeLabel), ct);
            diskTotalTask = QueryScalarAsync(PromQL.LinuxDiskTotalBytes(hostname, _options.ScrapeLabel), ct);
            uptimeTask = QueryUptimeLinuxAsync(hostname, ct);
        }

        await Task.WhenAll(cpuTask, memAvailTask, memTotalTask, diskFreeTask, diskTotalTask, uptimeTask);

        var memAvail = await memAvailTask;
        var memTotal = await memTotalTask;
        var uptimeSeconds = await uptimeTask;

        return new EndpointMetrics(
            Hostname: hostname,
            CpuUsagePercent: await cpuTask,
            MemoryUsedBytes: memTotal.HasValue && memAvail.HasValue ? memTotal.Value - memAvail.Value : null,
            MemoryTotalBytes: memTotal,
            DiskFreeBytes: await diskFreeTask,
            DiskTotalBytes: await diskTotalTask,
            Uptime: uptimeSeconds.HasValue ? TimeSpan.FromSeconds(uptimeSeconds.Value) : null,
            SampledAtUtc: DateTime.UtcNow);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<double?> QueryScalarAsync(string query, CancellationToken ct)
    {
        var vectors = await _client.QueryAsync(query, ct);
        return vectors.Count > 0 ? vectors[0].Value.AsDouble() : null;
    }

    private async Task<double?> QueryUptimeLinuxAsync(string hostname, CancellationToken ct)
    {
        // Linux uptime = time() - node_boot_time_seconds
        var bootTimeVectors = await _client.QueryAsync(
            PromQL.LinuxBootTimeSeconds(hostname, _options.ScrapeLabel), ct);
        var bootTime = bootTimeVectors.Count > 0 ? bootTimeVectors[0].Value.AsDouble() : null;
        if (bootTime is null) return null;
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - bootTime.Value;
    }

    private static ServiceState MapWindowsServiceState(IReadOnlyList<PrometheusVector> vectors)
    {
        // windows_exporter emits one row per state; find state=running where value=1
        foreach (var v in vectors)
        {
            if (v.Value.AsDouble() is not 1.0) continue;
            if (v.Metric.TryGetValue("state", out var state))
                return MapWindowsStateString(state);
        }
        return ServiceState.Unknown;
    }

    private static ServiceState MapLinuxServiceState(IReadOnlyList<PrometheusVector> vectors)
    {
        // node_exporter emits one row per state; find state=active where value=1
        foreach (var v in vectors)
        {
            if (v.Value.AsDouble() is not 1.0) continue;
            if (v.Metric.TryGetValue("state", out var state))
                return MapLinuxStateString(state);
        }
        return ServiceState.Unknown;
    }

    private static ServiceState MapWindowsStateString(string state) => state switch
    {
        "running" => ServiceState.Running,
        "stopped" => ServiceState.Stopped,
        "start_pending" => ServiceState.Starting,
        "stop_pending" => ServiceState.Stopping,
        "paused" => ServiceState.Paused,
        _ => ServiceState.Unknown
    };

    private static ServiceState MapLinuxStateString(string state) => state switch
    {
        "active" => ServiceState.Running,
        "activating" => ServiceState.Starting,
        "deactivating" => ServiceState.Stopping,
        "inactive" => ServiceState.Stopped,
        "failed" => ServiceState.Stopped,
        _ => ServiceState.Unknown
    };
}
