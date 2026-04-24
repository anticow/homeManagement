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

public static class PatchingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent-gateway-based <see cref="IPatchService"/> implementation.
    /// Use this in tests or environments where Action1 is unavailable but agent-based
    /// patch detection through the gateway is still required.
    /// </summary>
    public static IServiceCollection AddAgentBasedPatchService(this IServiceCollection services)
    {
        services.AddScoped<HomeManagement.Abstractions.Interfaces.IPatchService, PatchService>();
        return services;
    }
}
