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
