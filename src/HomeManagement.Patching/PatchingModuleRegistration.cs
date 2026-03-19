using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Patching;

public sealed class PatchingModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Patching";

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<LinuxPatchStrategy>();
        services.AddSingleton<WindowsPatchStrategy>();
        services.AddScoped<IPatchService, PatchService>();
    }
}
