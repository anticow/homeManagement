using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Integration.Prometheus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Services;

/// <summary>
/// Registers health monitoring services.
/// </summary>
public static class HealthMonitoringRegistration
{
    /// <summary>
    /// Register IAgentHealthService and related dependencies.
    /// Only registers when Prometheus is enabled; otherwise registers a no-op provider.
    /// Note: Configuration binding must be done by the caller (see ServicesModuleRegistration).
    /// </summary>
    public static IServiceCollection AddHealthMonitoring(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var prometheusOptions = ReadOptions<PrometheusOptions>(configuration, PrometheusOptions.Section);

        if (prometheusOptions.Enabled)
        {
            services.AddScoped<IAgentHealthService, AgentHealthService>();
        }
        else
        {
            // Register no-op service when Prometheus is disabled
            services.AddScoped<IAgentHealthService, DisabledAgentHealthService>();
        }

        return services;
    }

    private static TOptions ReadOptions<TOptions>(IConfiguration configuration, string section)
        where TOptions : new()
        => configuration.GetSection(section).Get<TOptions>() ?? new TOptions();
}

/// <summary>
/// No-op health service used when Prometheus is disabled.
/// Returns all agents as healthy with no metrics.
/// </summary>
internal sealed class DisabledAgentHealthService : IAgentHealthService
{
    public Task<AgentHealthSummary> GetAgentHealthAsync(
        Guid machineId,
        string hostname,
        string osType,
        CancellationToken ct = default)
    {
        return Task.FromResult(new AgentHealthSummary
        {
            Id = machineId,
            Hostname = hostname,
            OsType = osType,
            IsOnline = true,
            OverallStatus = AgentHealthStatus.Healthy,
            CpuUsagePercent = null,
            MemoryUsagePercent = null,
            DiskUsagePercent = null,
            LastUpdatedUtc = DateTime.UtcNow,
            ErrorMessage = "Prometheus integration is disabled"
        });
    }

    public Task<IReadOnlyList<AgentHealthSummary>> GetAllAgentHealthAsync(
        IReadOnlyList<(Guid Id, string Hostname, string OsType)> machines,
        CancellationToken ct = default)
    {
        var result = machines.Select(m => new AgentHealthSummary
        {
            Id = m.Id,
            Hostname = m.Hostname,
            OsType = m.OsType,
            IsOnline = true,
            OverallStatus = AgentHealthStatus.Healthy,
            CpuUsagePercent = null,
            MemoryUsagePercent = null,
            DiskUsagePercent = null,
            LastUpdatedUtc = DateTime.UtcNow,
            ErrorMessage = "Prometheus integration is disabled"
        }).ToList();

        return Task.FromResult<IReadOnlyList<AgentHealthSummary>>(result);
    }

    public void ClearCache()
    {
        // No-op
    }
}
