using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
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
    }
}
