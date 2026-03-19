namespace HomeManagement.Abstractions.CrossCutting;

/// <summary>
/// Aggregates health status across subsystems (vault, database, transport, agents).
/// </summary>
public interface ISystemHealthService
{
    Task<SystemHealthReport> CheckAsync(CancellationToken ct = default);
}

public record SystemHealthReport(
    HealthStatus OverallStatus,
    IReadOnlyList<ComponentHealth> Components,
    DateTime CheckedAtUtc);

public record ComponentHealth(
    string ComponentName,
    HealthStatus Status,
    string? Detail,
    TimeSpan? Latency);

public enum HealthStatus { Healthy, Degraded, Unhealthy }
