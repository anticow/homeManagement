using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Inventory;

public sealed class InventoryModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Inventory";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<IInventoryService, InventoryService>();
    }
}
