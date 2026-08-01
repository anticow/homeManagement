using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Service for aggregating and caching agent health metrics from Prometheus.
/// Queries CPU, memory, disk, and availability metrics for all managed agents.
/// </summary>
public interface IAgentHealthService
{
    /// <summary>
    /// Get aggregated health summary for a single agent.
    /// Results are cached for the configured TTL.
    /// </summary>
    /// <param name="machineId">The machine ID to query.</param>
    /// <param name="hostname">The hostname of the agent (e.g., "sidobits.cowgomu.net").</param>
    /// <param name="osType">The operating system type (Windows, Linux, macOS).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Agent health summary with metrics and status.</returns>
    Task<AgentHealthSummary> GetAgentHealthAsync(
        Guid machineId,
        string hostname,
        string osType,
        CancellationToken ct = default);

    /// <summary>
    /// Get aggregated health summaries for all agents.
    /// Results are cached for the configured TTL.
    /// </summary>
    /// <param name="machines">Collection of machines to query (id, hostname, osType).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of agent health summaries.</returns>
    Task<IReadOnlyList<AgentHealthSummary>> GetAllAgentHealthAsync(
        IReadOnlyList<(Guid Id, string Hostname, string OsType)> machines,
        CancellationToken ct = default);

    /// <summary>
    /// Clear the metric cache. Called when metrics need to be refreshed immediately.
    /// </summary>
    void ClearCache();
}
