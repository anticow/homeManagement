using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Repositories;

public sealed class ServiceSnapshotRepository : IServiceSnapshotRepository
{
    private readonly HomeManagementDbContext _db;

    public ServiceSnapshotRepository(HomeManagementDbContext db) => _db = db;

    public async Task<IReadOnlyList<ServiceSnapshot>> GetByMachineAsync(Guid machineId, CancellationToken ct = default)
    {
        var entities = await _db.ServiceSnapshots
            .Where(s => s.MachineId == machineId)
            .OrderByDescending(s => s.CapturedUtc)
            .ToListAsync(ct);

        return entities.Select(ToDomain).ToList();
    }

    public async Task<ServiceSnapshot?> GetLatestAsync(Guid machineId, string serviceName, CancellationToken ct = default)
    {
        var entity = await _db.ServiceSnapshots
            .Where(s => s.MachineId == machineId && s.ServiceName == serviceName)
            .OrderByDescending(s => s.CapturedUtc)
            .FirstOrDefaultAsync(ct);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task AddAsync(ServiceSnapshot snapshot, CancellationToken ct = default)
    {
        var entity = new ServiceSnapshotEntity
        {
            Id = snapshot.Id,
            MachineId = snapshot.MachineId,
            ServiceName = snapshot.ServiceName,
            DisplayName = snapshot.DisplayName,
            State = snapshot.State,
            StartupType = snapshot.StartupType,
            ProcessId = snapshot.ProcessId,
            CapturedUtc = snapshot.CapturedUtc
        };
        await _db.ServiceSnapshots.AddAsync(entity, ct);
    }

    private static ServiceSnapshot ToDomain(ServiceSnapshotEntity e) => new(
        e.Id, e.MachineId, e.ServiceName, e.DisplayName,
        e.State, e.StartupType, e.ProcessId, e.CapturedUtc);
}
