using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;

namespace HomeManagement.Broker.Host.Services;

/// <summary>Periodic platform composition only. No request path invokes this collector.</summary>
public sealed partial class DeviceStateCollector(IInventoryService inventory, IMachineAgentRuntime runtime,
    IFreshAgentSnapshotReader freshLegacy, IDeviceStateRepository states, IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<DeviceStateCollector> logger)
{
    private const string AgentUnavailable = "Gateway validation unavailable.";
    private const string MetricsUnavailable = "Prometheus/exporter metrics unavailable.";

    public async Task ValidateAsync(CancellationToken ct)
    {
        // Durable fair queue: attempts are committed before network work, so even interrupted
        // machines move behind unvisited/older attempts on the next cycle (and after restart).
        // Rebuild by machine GUID, not page offset, to tolerate additions, deletion and renames.
        var work = new Dictionary<Guid, (Machine Machine, DateTimeOffset? AttemptUtc)>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await inventory.QueryAsync(new MachineQuery(Page: pageNumber, PageSize: 100), ct);
            var snapshots = await states.GetForMachinesAsync(page.Items.Select(m => m.Id).ToArray(), ct);
            foreach (var machine in page.Items.Where(m => !m.IsDeleted))
                work.TryAdd(machine.Id, (machine, snapshots.GetValueOrDefault(machine.Id)?.MetricsAttemptUtc));
            if (page.Items.Count == 0 || (long)page.Page * page.PageSize >= page.TotalCount) break;
        }

        bool? managed = null;
        IReadOnlyList<ConnectedAgent>? legacy = null;
        DateTimeOffset? legacyValidatedAt = null;
        try
        {
            managed = await runtime.VerifyEnrollmentModeAsync(ct);
            if (managed == false)
            {
                legacy = await freshLegacy.ReadFreshLegacySnapshotAsync(ct);
                legacyValidatedAt = clock.GetUtcNow();
            }
        }
#pragma warning disable CA1031 // Treat a source failure as unavailable evidence, never as disconnected or a fallback.
        catch (Exception) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            // Mode verification or fresh list failure never falls back to the gateway's cached list.
            LogGatewayUnavailable(logger);
        }

        await Parallel.ForEachAsync(work.Values.OrderBy(w => w.AttemptUtc).ThenBy(w => w.Machine.Id),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (item, token) =>
            {
                var machine = item.Machine;
                try { await ValidateMachineAsync(machine, managed, legacy, legacyValidatedAt, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
#pragma warning disable CA1031 // Isolate persistence/unexpected per-machine failures; later machines must still validate.
                catch (Exception)
#pragma warning restore CA1031
                {
                    LogMachineUnavailable(logger, machine.Id);
                }
            });
    }

    private async Task ValidateMachineAsync(Machine machine, bool? managed, IReadOnlyList<ConnectedAgent>? legacy,
        DateTimeOffset? legacyValidatedAt, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        // The fair work list is a planning snapshot; recheck current inventory before
        // contacting sources so queued deletions/transport changes/renames are respected.
        var current = await scope.ServiceProvider.GetRequiredService<IMachineRepository>().GetByIdAsync(machine.Id, ct);
        if (current is null || current.IsDeleted) return;
        machine = current;
        var repository = scope.ServiceProvider.GetRequiredService<IDeviceStateRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var previous = await repository.GetAsync(machine.Id, ct) ?? new DeviceStateSnapshot { MachineId = machine.Id };
        var attempt = clock.GetUtcNow();
        var attempted = previous with
        {
            AgentAttemptUtc = machine.Protocol == TransportProtocol.Agent ? attempt : previous.AgentAttemptUtc,
            MetricsAttemptUtc = attempt
        };
        // Persist attempts first, even if shutdown interrupts the subsequent network work.
        await repository.UpsertAsync(attempted, ct);
        await uow.SaveChangesAsync(ct);
        var agentTask = ValidateAgentAsync(machine, attempted, managed, legacy, legacyValidatedAt, ct);
        var metricsTask = ValidateMetricsAsync(machine, attempted,
            scope.ServiceProvider.GetRequiredService<IAgentHealthService>(), ct);
        await Task.WhenAll(agentTask, metricsTask);
        var agent = await agentTask;
        var metrics = await metricsTask;
        var updated = agent with
        {
            MetricsAttemptUtc = metrics.MetricsAttemptUtc,
            MetricsSuccessUtc = metrics.MetricsSuccessUtc,
            MetricsFailure = metrics.MetricsFailure,
            Health = metrics.Health
        };
        await repository.UpsertAsync(updated, ct);
        await uow.SaveChangesAsync(ct);
    }

    private async Task<DeviceStateSnapshot> ValidateAgentAsync(Machine machine, DeviceStateSnapshot previous,
        bool? managed, IReadOnlyList<ConnectedAgent>? legacy, DateTimeOffset? legacyValidatedAt, CancellationToken ct)
    {
        if (machine.Protocol != TransportProtocol.Agent) return previous;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (managed is null || managed == false && legacy is null)
                return previous with { AgentFailure = AgentUnavailable };
            if (managed.Value)
            {
                var observation = await runtime.GetMachineObservationAsync(machine.Id, budget.Token);
                // Runtime enforces authority/mode. Check the bound result again at the persistence boundary.
                if (observation.MachineId != machine.Id || observation.RegistryId == Guid.Empty
                    || observation.BindingVersion < 1 || observation.CredentialGeneration < 1
                    || string.IsNullOrWhiteSpace(observation.AgentId) || observation.Connected != observation.SessionId.HasValue)
                    throw new InvalidOperationException("Invalid bound observation.");
                return previous with
                {
                    AgentSuccessUtc = clock.GetUtcNow(),
                    AgentFailure = null,
                    LastKnownConnected = observation.Connected,
                    LastHeartbeatUtc = observation.LastHeartbeatUtc ?? previous.LastHeartbeatUtc,
                    ManagedObservation = observation
                };
            }
            // Same exact legacy resolution order as AgentTransportProvider; never prefix/shortname matching.
            var match = legacy?.FirstOrDefault(a => string.Equals(a.Hostname, machine.Hostname.Value, StringComparison.OrdinalIgnoreCase))
                ?? legacy?.FirstOrDefault(a => string.Equals(a.AgentId, machine.Hostname.Value, StringComparison.OrdinalIgnoreCase));
            return previous with
            {
                AgentSuccessUtc = legacyValidatedAt,
                AgentFailure = null,
                LastKnownConnected = match is not null,
                // Older gateways' historical LastHeartbeatUtc initializes at connection. Never call that an actual heartbeat.
                LastHeartbeatUtc = match?.LastReceivedHeartbeatUtc is not { } heartbeat || heartbeat == default
                    ? previous.LastHeartbeatUtc : new DateTimeOffset(DateTime.SpecifyKind(heartbeat, DateTimeKind.Utc)),
                ManagedObservation = null
            };
        }
#pragma warning disable CA1031 // Keep each source independent; only caller cancellation aborts the machine operation.
        catch (Exception) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return previous with { AgentFailure = AgentUnavailable };
        }
    }

    private static async Task<DeviceStateSnapshot> ValidateMetricsAsync(Machine machine, DeviceStateSnapshot previous,
        IAgentHealthService healthService, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var health = await healthService.GetAgentHealthAsync(machine.Id, machine.Hostname.Value, machine.OsType.ToString(), budget.Token);
            // The existing health service uses missing `up` and upstream failures interchangeably.
            // Do not certify those responses (or disabled monitoring) as fresh successful metrics.
            if (health is null || health.Id != machine.Id || !string.IsNullOrEmpty(health.ErrorMessage) || !health.IsOnline
                || !ValidPercent(health.CpuUsagePercent) || !ValidPercent(health.MemoryUsagePercent) || !ValidPercent(health.DiskUsagePercent)
                || !(health.CpuUsagePercent.HasValue || health.MemoryUsagePercent.HasValue || health.DiskUsagePercent.HasValue))
                return previous with { MetricsFailure = MetricsUnavailable };
            return previous with
            {
                MetricsSuccessUtc = new DateTimeOffset(DateTime.SpecifyKind(health.LastUpdatedUtc, DateTimeKind.Utc)),
                MetricsFailure = null,
                Health = health
            };
        }
#pragma warning disable CA1031 // Keep agent successes independently persistable even if a metrics provider fails unexpectedly.
        catch (Exception) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return previous with { MetricsFailure = MetricsUnavailable };
        }
    }

    private static bool ValidPercent(double? value) =>
        value is null || double.IsFinite(value.Value) && value.Value is >= 0 and <= 100;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device-state gateway authority or fresh snapshot validation unavailable.")]
    private static partial void LogGatewayUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device-state persistence or validation failed for {MachineId}.")]
    private static partial void LogMachineUnavailable(ILogger logger, Guid machineId);
}
