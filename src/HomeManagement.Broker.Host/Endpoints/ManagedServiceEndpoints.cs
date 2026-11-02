using System.Security.Claims;
using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Auth;

namespace HomeManagement.Broker.Host.Endpoints;

public static class ManagedServiceEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapManagedServiceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/machines/{machineId:guid}/managed-services")
            .WithTags("Managed services").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.Request.IsHttps)
                return Results.Problem("Managed service operations require HTTPS.", statusCode: 403);
            return await next(context);
        });

        group.MapGet("/", async (Guid machineId, HttpContext context,
            IInventoryService inventory, IRemoteExecutor executor, CancellationToken ct) =>
        {
            if (!HasPermission(context, Permissions.ServicesRead))
                return Results.Forbid();
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null) return Results.NotFound();
            return await DispatchAsync(machine, null, executor, ct);
        });

        group.MapPost("/control", async (Guid machineId, ManagedServiceRequest request, HttpContext context,
            IInventoryService inventory, IRemoteExecutor executor, IAuditLogger audit,
            ICorrelationContext correlation, CancellationToken ct) =>
        {
            if (!HasPermission(context, Permissions.ServicesControl)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 80 || !Enum.IsDefined(request.Action))
                return Results.BadRequest(new { error = "An enrolled ID and Start or Stop action are required." });
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null) return Results.NotFound();
            var auditEvent = CreateAudit(machine, context, correlation,
                request.Action == ManagedServiceAction.Start ? AuditAction.ServiceStarted : AuditAction.ServiceStopped,
                $"Managed service {request.Id}: {request.Action}");
            // Persist intent before dispatch; unavailable auditing must not permit an unaudited mutation.
            await audit.RecordAsync(auditEvent, ct);
            var result = await DispatchAsync(machine, request, executor, ct);
            await audit.RecordAsync(auditEvent with
            {
                EventId = Guid.NewGuid(),
                TimestampUtc = DateTime.UtcNow,
                Outcome = result is Microsoft.AspNetCore.Http.HttpResults.Ok<IReadOnlyList<ManagedServiceSnapshot>>
                    ? AuditOutcome.Success : AuditOutcome.Failure,
                Detail = $"Completed managed service {request.Id}: {request.Action}"
            }, CancellationToken.None);
            return result;
        });

        group.MapPost("/break-peering", async (Guid machineId, HttpContext context,
            IInventoryService inventory, IAgentPeeringControl peering, IAuditLogger audit,
            ICorrelationContext correlation, CancellationToken ct) =>
        {
            if (!HasPermission(context, Permissions.AdminSettings)) return Results.Forbid();
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null) return Results.NotFound();
            if (machine.Protocol != TransportProtocol.Agent)
                return Results.BadRequest(new { error = "This machine does not use Agent transport." });
            var auditEvent = CreateAudit(machine, context, correlation, AuditAction.AgentDisconnected, "Break agent peering requested");
            await audit.RecordAsync(auditEvent, ct);
            await peering.RevokeAsync(machine.Hostname.Value, ct);
            await audit.RecordAsync(auditEvent with
            {
                EventId = Guid.NewGuid(), TimestampUtc = DateTime.UtcNow,
                Detail = "Agent peering revoked", Outcome = AuditOutcome.Success
            }, CancellationToken.None);
            return Results.Ok();
        });
    }

    private static bool HasPermission(HttpContext context, string permission) =>
        RbacService.HasPermission(context.User.FindAll(ClaimTypes.Role).Select(claim => claim.Value), permission);

    private static AuditEvent CreateAudit(Machine machine, HttpContext context, ICorrelationContext correlation,
        AuditAction action, string detail) => new(
        Guid.NewGuid(), DateTime.UtcNow, correlation.CorrelationId, action,
        context.User.Identity?.Name ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "authenticated user",
        machine.Id, machine.Hostname.Value, detail, null, AuditOutcome.PartialSuccess, null);

    private static async Task<IResult> DispatchAsync(Machine machine, ManagedServiceRequest? request,
        IRemoteExecutor executor, CancellationToken ct)
    {
        if (machine.Protocol != TransportProtocol.Agent)
            return Results.BadRequest(new { error = "Managed services require Agent transport." });
        var target = new MachineTarget(machine.Id, machine.Hostname, machine.OsType,
            machine.ConnectionMode, machine.Protocol, machine.Port, machine.CredentialId);
        var command = new RemoteCommand(request is null ? string.Empty : JsonSerializer.Serialize(request, JsonOptions),
            TimeSpan.FromSeconds(45), CommandType: "ManagedServices");
        var result = await executor.ExecuteAsync(target, command, ct);
        if (result.ExitCode != 0 || result.TimedOut)
            return Results.Problem(result.Stderr, statusCode: 409, title: "Managed service operation failed");
        var snapshots = JsonSerializer.Deserialize<IReadOnlyList<ManagedServiceSnapshot>>(result.Stdout, JsonOptions)
            ?? throw new InvalidOperationException("Agent returned an empty managed service response.");
        return Results.Ok(snapshots);
    }
}
