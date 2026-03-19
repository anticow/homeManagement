using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Vault;

public sealed class VaultModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Vault";

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<ICredentialVault, CredentialVaultService>();
    }
}
