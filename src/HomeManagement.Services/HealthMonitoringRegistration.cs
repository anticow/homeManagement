using HomeManagement.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Services;

/// <summary>
/// Registers health monitoring services.
/// </summary>
public static class HealthMonitoringRegistration
{
    /// <summary>
    /// Register IAgentHealthService and related dependencies.
    /// Note: Configuration binding must be done by the caller (see ServicesModuleRegistration).
    /// </summary>
    public static IServiceCollection AddHealthMonitoring(
        this IServiceCollection services)
    {
        services.AddScoped<IAgentHealthService, AgentHealthService>();
        return services;
    }
}
