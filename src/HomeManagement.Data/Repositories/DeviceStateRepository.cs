using System.Text.Json;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Repositories;

public sealed class DeviceStateRepository(HomeManagementDbContext db) : IDeviceStateRepository
{
    public async Task<DeviceStateSnapshot?> GetAsync(Guid machineId, CancellationToken ct = default)
    {
        var entity = await db.DeviceStates.AsNoTracking().SingleOrDefaultAsync(s => s.MachineId == machineId, ct);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<IReadOnlyDictionary<Guid, DeviceStateSnapshot>> GetForMachinesAsync(IReadOnlyList<Guid> machineIds, CancellationToken ct = default)
    {
        var entities = await db.DeviceStates.AsNoTracking().Where(s => machineIds.Contains(s.MachineId)).ToListAsync(ct);
        return entities.ToDictionary(s => s.MachineId, ToDomain);
    }

    public async Task UpsertAsync(DeviceStateSnapshot snapshot, CancellationToken ct = default)
    {
        // A machine may have been soft-deleted while validation was in flight.
        if (!await db.Machines.AnyAsync(m => m.Id == snapshot.MachineId, ct)) return;
        var entity = await db.DeviceStates.SingleOrDefaultAsync(s => s.MachineId == snapshot.MachineId, ct);
        if (entity is null)
        {
            entity = new DeviceStateEntity { MachineId = snapshot.MachineId };
            await db.DeviceStates.AddAsync(entity, ct);
        }
        entity.AgentAttemptUtc = snapshot.AgentAttemptUtc?.UtcDateTime;
        entity.AgentSuccessUtc = snapshot.AgentSuccessUtc?.UtcDateTime;
        entity.LastHeartbeatUtc = snapshot.LastHeartbeatUtc?.UtcDateTime;
        entity.LastKnownConnected = snapshot.LastKnownConnected;
        entity.AgentFailure = snapshot.AgentFailure;
        entity.ManagedObservationJson = snapshot.ManagedObservation is null ? null : JsonSerializer.Serialize(snapshot.ManagedObservation);
        entity.MetricsAttemptUtc = snapshot.MetricsAttemptUtc?.UtcDateTime;
        entity.MetricsSuccessUtc = snapshot.MetricsSuccessUtc?.UtcDateTime;
        entity.HealthJson = snapshot.Health is null ? null : JsonSerializer.Serialize(snapshot.Health);
        entity.MetricsFailure = snapshot.MetricsFailure;
    }

    private static DateTimeOffset? Utc(DateTime? value) =>
        value is { } time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : null;

    private static DeviceStateSnapshot ToDomain(DeviceStateEntity entity) => new()
    {
        MachineId = entity.MachineId,
        AgentAttemptUtc = Utc(entity.AgentAttemptUtc),
        AgentSuccessUtc = Utc(entity.AgentSuccessUtc),
        LastHeartbeatUtc = Utc(entity.LastHeartbeatUtc),
        LastKnownConnected = entity.LastKnownConnected,
        AgentFailure = entity.AgentFailure,
        ManagedObservation = entity.ManagedObservationJson is null ? null : JsonSerializer.Deserialize<MachineAgentObservation>(entity.ManagedObservationJson),
        MetricsAttemptUtc = Utc(entity.MetricsAttemptUtc),
        MetricsSuccessUtc = Utc(entity.MetricsSuccessUtc),
        Health = entity.HealthJson is null ? null : JsonSerializer.Deserialize<AgentHealthSummary>(entity.HealthJson),
        MetricsFailure = entity.MetricsFailure
    };
}
