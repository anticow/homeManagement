using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Core;

/// <summary>
/// Aggregates health status across subsystems: database, vault, transport circuit-breakers.
/// </summary>
internal sealed class SystemHealthService(
    IServiceScopeFactory scopeFactory,
    IResiliencePipeline resilience,
    ILogger<SystemHealthService> logger) : ISystemHealthService
{
    public async Task<SystemHealthReport> CheckAsync(CancellationToken ct = default)
    {
        var components = new List<ComponentHealth>();

        components.Add(await CheckDatabaseAsync(ct));
        components.Add(CheckResilience());

        var overall = components.Any(c => c.Status == HealthStatus.Unhealthy)
            ? HealthStatus.Unhealthy
            : components.Any(c => c.Status == HealthStatus.Degraded)
                ? HealthStatus.Degraded
                : HealthStatus.Healthy;

        return new SystemHealthReport(overall, components, DateTime.UtcNow);
    }

    private async Task<ComponentHealth> CheckDatabaseAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HomeManagementDbContext>();
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            sw.Stop();
            return new ComponentHealth("Database", HealthStatus.Healthy, "Responsive", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
#pragma warning disable CA1848 // Use LoggerMessage delegates
            logger.LogWarning(ex, "Database health check failed");
#pragma warning restore CA1848
            return new ComponentHealth("Database", HealthStatus.Unhealthy, ex.Message, sw.Elapsed);
        }
    }

    private ComponentHealth CheckResilience()
    {
        // If any circuit breaker is open, mark transport as degraded
        var state = resilience.GetCircuitState("*");
        var status = state switch
        {
            CircuitState.Open => HealthStatus.Unhealthy,
            CircuitState.HalfOpen => HealthStatus.Degraded,
            _ => HealthStatus.Healthy
        };
        return new ComponentHealth("Transport", status, $"Circuit: {state}", null);
    }
}
