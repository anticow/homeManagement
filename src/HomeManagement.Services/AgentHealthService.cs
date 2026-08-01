using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Integration.Prometheus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Services;

/// <summary>
/// Options for agent health monitoring configuration.
/// </summary>
public sealed class AgentHealthOptions
{
    public const string SectionName = "HealthMonitoring";

    /// <summary>Enable health monitoring. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cache TTL in minutes. Default: 5.</summary>
    public int CacheMinutes { get; set; } = 5;

    /// <summary>Threshold for critical status (%). Default: 85.</summary>
    public int CriticalThresholdPercent { get; set; } = 85;

    /// <summary>Threshold for warning status (%). Default: 70.</summary>
    public int WarningThresholdPercent { get; set; } = 70;

    /// <summary>Query timeout in seconds. Default: 10.</summary>
    public int QueryTimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Aggregates agent health metrics from Prometheus with caching.
/// Queries CPU, memory, disk usage, and availability for all agents.
/// </summary>
public sealed class AgentHealthService : IAgentHealthService
{
    private readonly PrometheusClient _prometheus;
    private readonly PrometheusOptions _prometheusOptions;
    private readonly AgentHealthOptions _options;
    private readonly ILogger<AgentHealthService> _logger;
    private readonly Dictionary<string, CachedHealth> _cache = new();
    private readonly object _cacheLock = new();

    public AgentHealthService(
        PrometheusClient prometheus,
        IOptions<PrometheusOptions> prometheusOptions,
        IOptions<AgentHealthOptions> options,
        ILogger<AgentHealthService> logger)
    {
        _prometheus = prometheus;
        _prometheusOptions = prometheusOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AgentHealthSummary> GetAgentHealthAsync(
        Guid machineId,
        string hostname,
        string osType,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return new AgentHealthSummary
            {
                Id = machineId,
                Hostname = hostname,
                OsType = osType,
                IsOnline = true,
                OverallStatus = AgentHealthStatus.Healthy,
                ErrorMessage = "Health monitoring disabled"
            };
        }

        var cacheKey = hostname.ToLowerInvariant();

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired(_options.CacheMinutes))
            {
                _logger.LogDebug("Health cache hit for {Hostname}", hostname);
                var cachedSummary = cached.Summary;
                cachedSummary.Id = machineId;
                return cachedSummary;
            }
        }

        var summary = await QueryAgentHealthAsync(machineId, hostname, osType, ct);

        lock (_cacheLock)
        {
            _cache[cacheKey] = new CachedHealth(summary, DateTime.UtcNow);
        }

        return summary;
    }

    public async Task<IReadOnlyList<AgentHealthSummary>> GetAllAgentHealthAsync(
        IReadOnlyList<(Guid Id, string Hostname, string OsType)> machines,
        CancellationToken ct = default)
    {
        var tasks = machines
            .Select(m => GetAgentHealthAsync(m.Id, m.Hostname, m.OsType, ct))
            .ToList();

        await Task.WhenAll(tasks);

        return tasks.Select(t => t.Result).ToList();
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _logger.LogInformation("Agent health cache cleared");
        }
    }

    private async Task<AgentHealthSummary> QueryAgentHealthAsync(
        Guid machineId,
        string hostname,
        string osType,
        CancellationToken ct)
    {
        _logger.LogDebug("Querying health metrics for {Hostname} ({OsType})", hostname, osType);

        var summary = new AgentHealthSummary
        {
            Id = machineId,
            Hostname = hostname,
            OsType = osType,
            LastUpdatedUtc = DateTime.UtcNow
        };

        // Check if agent is online
        var upQuery = PromQL.EndpointUp(hostname, _prometheusOptions.ScrapeLabel);
        var upResult = await QuerySingleValueAsync(upQuery, ct);
        summary.IsOnline = upResult.HasValue && upResult.Value > 0.5;

        if (!summary.IsOnline)
        {
            summary.OverallStatus = AgentHealthStatus.Critical;
            summary.ErrorMessage = "Agent offline or unreachable";
            _logger.LogWarning("Agent {Hostname} is offline", hostname);
            return summary;
        }

        try
        {
            // Query metrics based on OS type
            switch (osType.ToLowerInvariant())
            {
                case "windows":
                    await QueryWindowsMetricsAsync(hostname, summary, ct);
                    break;

                case "linux":
                    await QueryLinuxMetricsAsync(hostname, summary, ct);
                    break;

                case "macos":
                    await QueryMacOsMetricsAsync(hostname, summary, ct);
                    break;

                default:
                    summary.ErrorMessage = $"Unknown OS type: {osType}";
                    summary.OverallStatus = AgentHealthStatus.Critical;
                    _logger.LogWarning("Unknown OS type {OsType} for {Hostname}", osType, hostname);
                    return summary;
            }

            // Determine overall status based on thresholds
            summary.OverallStatus = DetermineStatus(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying metrics for {Hostname}", hostname);
            summary.ErrorMessage = ex.Message;
            summary.OverallStatus = AgentHealthStatus.Critical;
        }

        return summary;
    }

    private async Task QueryWindowsMetricsAsync(
        string hostname,
        AgentHealthSummary summary,
        CancellationToken ct)
    {
        var cpuQuery = PromQL.WindowsCpuUsagePercent(hostname, _prometheusOptions.ScrapeLabel);
        var memQuery = PromQL.WindowsMemoryUsagePercent(hostname, _prometheusOptions.ScrapeLabel);
        var diskQuery = PromQL.WindowsDiskUsagePercent(hostname, _prometheusOptions.ScrapeLabel);

        summary.CpuUsagePercent = await QuerySingleValueAsync(cpuQuery, ct);
        summary.MemoryUsagePercent = await QuerySingleValueAsync(memQuery, ct);
        summary.DiskUsagePercent = await QuerySingleValueAsync(diskQuery, ct);
    }

    private async Task QueryLinuxMetricsAsync(
        string hostname,
        AgentHealthSummary summary,
        CancellationToken ct)
    {
        var cpuQuery = PromQL.LinuxCpuUsagePercent(hostname, _prometheusOptions.ScrapeLabel);
        var memQuery = PromQL.LinuxMemoryUsagePercent(hostname, _prometheusOptions.ScrapeLabel);
        var diskQuery = PromQL.LinuxDiskUsagePercent(hostname, _prometheusOptions.ScrapeLabel);

        summary.CpuUsagePercent = await QuerySingleValueAsync(cpuQuery, ct);
        summary.MemoryUsagePercent = await QuerySingleValueAsync(memQuery, ct);
        summary.DiskUsagePercent = await QuerySingleValueAsync(diskQuery, ct);
    }

    private async Task QueryMacOsMetricsAsync(
        string hostname,
        AgentHealthSummary summary,
        CancellationToken ct)
    {
        var cpuQuery = PromQL.MacOsCpuUsagePercent(hostname, _prometheusOptions.ScrapeLabel);
        var memQuery = PromQL.MacOsMemoryUsagePercent(hostname, _prometheusOptions.ScrapeLabel);
        var diskQuery = PromQL.MacOsDiskUsagePercent(hostname, _prometheusOptions.ScrapeLabel);

        summary.CpuUsagePercent = await QuerySingleValueAsync(cpuQuery, ct);
        summary.MemoryUsagePercent = await QuerySingleValueAsync(memQuery, ct);
        summary.DiskUsagePercent = await QuerySingleValueAsync(diskQuery, ct);
    }

    private async Task<double?> QuerySingleValueAsync(string promql, CancellationToken ct)
    {
        var results = await _prometheus.QueryAsync(promql, ct);

        if (results.Count == 0)
        {
            return null;
        }

        return results[0].Value.AsDouble();
    }

    private AgentHealthStatus DetermineStatus(AgentHealthSummary summary)
    {
        if (!summary.IsOnline)
        {
            return AgentHealthStatus.Critical;
        }

        var metrics = new[] { summary.CpuUsagePercent, summary.MemoryUsagePercent, summary.DiskUsagePercent }
            .Where(m => m.HasValue)
            .Select(m => m!.Value)
            .ToList();

        if (metrics.Count == 0)
        {
            return AgentHealthStatus.Healthy; // No metrics collected but online
        }

        if (metrics.Any(m => m > _options.CriticalThresholdPercent))
        {
            return AgentHealthStatus.Critical;
        }

        if (metrics.Any(m => m > _options.WarningThresholdPercent))
        {
            return AgentHealthStatus.Warning;
        }

        return AgentHealthStatus.Healthy;
    }

    private sealed record CachedHealth(AgentHealthSummary Summary, DateTime CachedAtUtc)
    {
        public bool IsExpired(int ttlMinutes) =>
            DateTime.UtcNow.Subtract(CachedAtUtc).TotalMinutes > ttlMinutes;
    }
}
