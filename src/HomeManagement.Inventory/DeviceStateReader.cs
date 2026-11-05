using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using Microsoft.Extensions.Options;

namespace HomeManagement.Inventory;

internal sealed class DeviceStateReader(IMachineRepository inventory, IDeviceStateRepository states,
    IOptions<DeviceStateOptions> options, TimeProvider clock) : IDeviceStateReader
{
    public async Task<PagedResult<FleetDeviceState>> QueryAsync(MachineQuery query, CancellationToken ct = default)
    {
        var machines = await inventory.QueryAsync(query with { IncludeDeleted = false }, ct);
        var snapshots = await states.GetForMachinesAsync(machines.Items.Select(m => m.Id).ToArray(), ct);
        var now = clock.GetUtcNow();
        var items = machines.Items.Where(m => !m.IsDeleted).Select(machine =>
        {
            var snapshot = snapshots.GetValueOrDefault(machine.Id) ?? new DeviceStateSnapshot { MachineId = machine.Id };
            var agentAge = Age(snapshot.AgentSuccessUtc, now);
            var metricsAge = Age(snapshot.MetricsSuccessUtc, now);
            var stale = agentAge is null || agentAge >= options.Value.StaleAfterSeconds;
            var applicable = machine.Protocol == TransportProtocol.Agent;
            var connectivity = !applicable ? DeviceConnectivity.NotApplicable
                : stale || snapshot.LastKnownConnected is null ? DeviceConnectivity.Unknown
                : snapshot.LastKnownConnected.Value ? DeviceConnectivity.Online : DeviceConnectivity.Offline;
            return new FleetDeviceState(machine, snapshot, connectivity, applicable ? agentAge : null,
                applicable && stale, metricsAge, metricsAge is null || metricsAge >= options.Value.StaleAfterSeconds);
        }).ToArray();
        return new(items, machines.TotalCount, machines.Page, machines.PageSize);
    }

    private static double? Age(DateTimeOffset? timestamp, DateTimeOffset now) =>
        timestamp.HasValue ? Math.Max(0, (now - timestamp.Value).TotalSeconds) : null;
}
