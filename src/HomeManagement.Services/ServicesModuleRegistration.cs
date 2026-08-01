using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Services;

public sealed class ServicesModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Services";

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<LinuxServiceStrategy>();
        services.AddSingleton<WindowsServiceStrategy>();
        services.AddScoped<IServiceController, ServiceControllerService>();
        services.AddScoped<IProcessListService, RemoteProcessListService>();
    }
}

/// <summary>
/// Configuration-aware registration of health monitoring.
/// Must be called after configuration is available.
/// </summary>
public static class HealthMonitoringConfigurationRegistration
{
    public static void AddHealthMonitoringWithConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHealthMonitoring(configuration);
    }
}
