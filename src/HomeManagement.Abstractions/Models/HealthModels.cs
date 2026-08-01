namespace HomeManagement.Abstractions.Models;

/// <summary>
/// Health status enumeration for agents.
/// </summary>
public enum AgentHealthStatus
{
    /// <summary>All metrics within acceptable ranges.</summary>
    Healthy = 0,

    /// <summary>One or more metrics approaching limits (70–85%).</summary>
    Warning = 1,

    /// <summary>One or more metrics exceeding limits (>85%) or agent unreachable.</summary>
    Critical = 2
}

/// <summary>
/// Aggregated health summary for an agent.
/// Computed from Prometheus metrics for CPU, memory, disk, and availability.
/// </summary>
public sealed class AgentHealthSummary
{
    /// <summary>Machine ID (GUID).</summary>
    public Guid Id { get; set; }

    /// <summary>Hostname of the agent (e.g. "sidobits.cowgomu.net").</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Operating system type: Windows, Linux, macOS.</summary>
    public string OsType { get; set; } = string.Empty;

    /// <summary>True if the agent is reachable and reporting metrics; false otherwise.</summary>
    public bool IsOnline { get; set; }

    /// <summary>CPU usage percentage (0–100). Null if metrics unavailable.</summary>
    public double? CpuUsagePercent { get; set; }

    /// <summary>Memory usage percentage (0–100). Null if metrics unavailable.</summary>
    public double? MemoryUsagePercent { get; set; }

    /// <summary>Disk usage percentage (0–100). Null if metrics unavailable.</summary>
    public double? DiskUsagePercent { get; set; }

    /// <summary>
    /// Overall health status derived from metric thresholds:
    /// Critical if any metric > 85% or agent is offline,
    /// Warning if any metric > 70%,
    /// Healthy otherwise.
    /// </summary>
    public AgentHealthStatus OverallStatus { get; set; }

    /// <summary>UTC timestamp when this summary was last updated from Prometheus.</summary>
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Optional error message if metric collection failed.</summary>
    public string? ErrorMessage { get; set; }
}
