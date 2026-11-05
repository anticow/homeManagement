using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>Reads stored inventory and evidence only. Must not validate against a gateway or metrics service.</summary>
public interface IDeviceStateReader
{
    Task<PagedResult<FleetDeviceState>> QueryAsync(MachineQuery query, CancellationToken ct = default);
}
