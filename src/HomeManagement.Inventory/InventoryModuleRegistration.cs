using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeManagement.Inventory;

public sealed class InventoryModuleRegistration : IModuleRegistration
{
    public string ModuleName => "Inventory";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IDeviceStateReader, DeviceStateReader>();
        services.AddOptions<DeviceStateOptions>();
        services.TryAddSingleton(TimeProvider.System);
    }
}
