using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Broker.Host.Endpoints;

/// <summary>
/// Service controller endpoints.
/// </summary>
public static class ServiceEndpoints
{
    public static void MapServiceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/services")
            .WithTags("Services")
            .RequireAuthorization();

        group.MapGet("/{machineId:guid}", async (Guid machineId, IServiceController controller, IInventoryService inventory, CancellationToken ct) =>
        {
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null) return Results.NotFound();

            var target = new MachineTarget(machine.Id, machine.Hostname, machine.OsType, machine.ConnectionMode, machine.Protocol, machine.Port, machine.CredentialId);
            var services = await controller.ListServicesAsync(target, ct: ct);
            return Results.Ok(services);
        });

        group.MapPost("/{machineId:guid}/control", async (Guid machineId, ServiceControlRequest request, IServiceController controller, IInventoryService inventory, CancellationToken ct) =>
        {
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null) return Results.NotFound();

            var target = new MachineTarget(machine.Id, machine.Hostname, machine.OsType, machine.ConnectionMode, machine.Protocol, machine.Port, machine.CredentialId);
            var result = await controller.ControlAsync(target, ServiceName.Create(request.ServiceName), request.Action, ct);
            return Results.Ok(result);
        });
    }
}

public sealed record ServiceControlRequest(string ServiceName, ServiceAction Action);
