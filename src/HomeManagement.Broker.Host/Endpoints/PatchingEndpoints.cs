using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Broker.Host.Endpoints;

/// <summary>
/// Patching endpoints.
/// </summary>
public static class PatchingEndpoints
{
    public static void MapPatchingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/patching")
            .WithTags("Patching")
            .RequireAuthorization();

        group.MapPost("/scan", async (PatchScanRequest request, IPatchService patchService, IInventoryService inventory, CancellationToken ct) =>
        {
            var machine = await inventory.GetAsync(request.MachineId, ct);
            if (machine is null) return Results.NotFound();

            var target = new MachineTarget(machine.Id, machine.Hostname, machine.OsType, machine.ConnectionMode, machine.Protocol, machine.Port, machine.CredentialId);
            var patches = await patchService.DetectAsync(target, ct);
            return Results.Ok(patches);
        });

        group.MapGet("/{machineId:guid}/history", async (Guid machineId, IPatchService patchService, CancellationToken ct) =>
        {
            var history = await patchService.GetHistoryAsync(machineId, ct);
            return Results.Ok(history);
        });
    }
}

public sealed record PatchScanRequest(Guid MachineId);
