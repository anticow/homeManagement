using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Broker.Host.Endpoints;

/// <summary>
/// Machine management endpoints.
/// </summary>
public static class MachineEndpoints
{
    public static void MapMachineEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/machines")
            .WithTags("Machines")
            .RequireAuthorization();

        group.MapGet("/", async (
            IInventoryService inventory,
            string? searchText,
            OsType? osType,
            MachineState? state,
            int page,
            int pageSize,
            CancellationToken ct) =>
        {
            var query = new MachineQuery
            {
                SearchText = searchText,
                OsType = osType,
                State = state,
                Page = page,
                PageSize = pageSize
            };
            return Results.Ok(await inventory.QueryAsync(query, ct));
        });

        group.MapGet("/{id:guid}", async (Guid id, IInventoryService inventory, CancellationToken ct) =>
        {
            var machine = await inventory.GetAsync(id, ct);
            return machine is not null ? Results.Ok(machine) : Results.NotFound();
        });

        group.MapPost("/", async (MachineCreateRequest request, IInventoryService inventory, CancellationToken ct) =>
        {
            var machine = await inventory.AddAsync(request, ct);
            return Results.Created($"/api/machines/{machine.Id}", machine);
        });

        group.MapDelete("/{id:guid}", async (Guid id, IInventoryService inventory, CancellationToken ct) =>
        {
            await inventory.RemoveAsync(id, ct);
            return Results.NoContent();
        });

        group.MapGet("/{id:guid}/state", async (
            Guid id,
            IInventoryService inventory,
            IEndpointStateProvider? stateProvider,
            CancellationToken ct) =>
        {
            var machine = await inventory.GetAsync(id, ct);
            if (machine is null) return Results.NotFound();

            if (stateProvider is null)
                return Results.Ok(new MachineStateSnapshot(false, null, null, null, null, null, null, DateTime.UtcNow));

            var onlineTask = stateProvider.GetEndpointOnlineAsync(machine.Hostname.Value, ct);
            var metricsTask = stateProvider.GetHardwareMetricsAsync(machine.Hostname.Value, machine.OsType, ct);
            await Task.WhenAll(onlineTask, metricsTask);

            var m = metricsTask.Result;
            return Results.Ok(new MachineStateSnapshot(
                onlineTask.Result,
                m?.CpuUsagePercent,
                m?.MemoryTotalBytes,
                m?.MemoryUsedBytes,
                m?.DiskTotalBytes,
                m?.DiskFreeBytes,
                m?.Uptime,
                DateTime.UtcNow));
        });

        group.MapGet("/summary", async (
            IInventoryService inventory,
            IEndpointStateProvider? stateProvider,
            CancellationToken ct) =>
        {
            var result = await inventory.QueryAsync(new MachineQuery { PageSize = 500 }, ct);
            var total = result.TotalCount;

            if (stateProvider is null)
                return Results.Ok(new MachineSummary(total, 0, total));

            var checks = await Task.WhenAll(
                result.Items.Select(m => stateProvider.GetEndpointOnlineAsync(m.Hostname.Value, ct)));

            var online = checks.Count(x => x);
            return Results.Ok(new MachineSummary(total, online, total - online));
        });

        group.MapPost("/{id:guid}/test", async (Guid id, IInventoryService inventory, IRemoteExecutor executor, CancellationToken ct) =>
        {
            var machine = await inventory.GetAsync(id, ct);
            if (machine is null) return Results.NotFound();

            var target = new MachineTarget(machine.Id, machine.Hostname, machine.OsType, machine.ConnectionMode, machine.Protocol, machine.Port, machine.CredentialId);
            var result = await executor.TestConnectionAsync(target, ct);
            return Results.Ok(result);
        });
    }
}
