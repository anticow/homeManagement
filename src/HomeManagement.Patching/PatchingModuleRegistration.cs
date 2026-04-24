using HomeManagement.Abstractions.CrossCutting;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Patching;

public sealed class PatchingModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Patching";

    public void Register(IServiceCollection services)
    {
        // Strategy singletons remain for backward-compat with tests and any remaining
        // internal consumers. IPatchService is registered by Action1IntegrationRegistration
        // (or DisabledAction1PatchService when Action1 is disabled) at the host level.
        services.AddSingleton<LinuxPatchStrategy>();
        services.AddSingleton<WindowsPatchStrategy>();
        // PatchService is NOT registered here — use Action1PatchService instead.
    }
}
