using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Repositories;

public interface IDeviceStateRepository
{
    Task<DeviceStateSnapshot?> GetAsync(Guid machineId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, DeviceStateSnapshot>> GetForMachinesAsync(IReadOnlyList<Guid> machineIds, CancellationToken ct = default);
    /// <summary>Stages changes; the caller commits using IUnitOfWork.</summary>
    Task UpsertAsync(DeviceStateSnapshot snapshot, CancellationToken ct = default);
}
