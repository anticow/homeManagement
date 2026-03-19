using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Abstractions.CrossCutting;

/// <summary>
/// Implemented by each module assembly to self-register its services.
/// Core discovers implementations via assembly scanning and invokes them during startup.
/// </summary>
public interface IModuleRegistration
{
    /// <summary>Display name of the module (for logging/diagnostics).</summary>
    string ModuleName { get; }

    /// <summary>Register this module's services into the DI container.</summary>
    void Register(IServiceCollection services);
}
