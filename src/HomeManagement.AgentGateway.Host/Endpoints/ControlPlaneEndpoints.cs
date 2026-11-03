using HomeManagement.Abstractions.Models;
using HomeManagement.AgentGateway.Host.Services;

namespace HomeManagement.AgentGateway.Host.Endpoints;

public static class ControlPlaneEndpoints
{
    public static void MapControlPlaneEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/agents");
        group.MapGet("/capabilities", (HttpContext context) => Results.Ok(new
        {
            enrollmentEnabled = context.RequestServices.GetService<AgentEnrollmentMode>()?.Enabled == true
        }));

        group.MapGet("/", GetConnectedAgents);
        group.MapGet("/{agentId}", GetAgentMetadata);
        group.MapPost("/{agentId}/commands", SendCommand);
        group.MapPost("/{agentId}/updates", RequestUpdate);
        var machines = group.MapGroup("/machines/{machineId:guid}");
        machines.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!context.HttpContext.Request.IsHttps)
                return Results.Problem("Machine-bound runtime requires HTTPS.", statusCode: 403);
            if (context.HttpContext.RequestServices.GetService<AgentEnrollmentMode>()?.Enabled != true)
                return Results.Conflict(new { error = "Machine-bound runtime requires managed enrollment." });
            return await next(context);
        });
        machines.MapGet("/observation", (Guid machineId, StandaloneAgentGatewayService gateway) =>
            Results.Ok(gateway.GetMachineObservation(machineId)));
        machines.MapGet("/metadata", (Guid machineId, StandaloneAgentGatewayService gateway) =>
            Results.Ok(gateway.GetMachineMetadata(machineId)));
        machines.MapPost("/commands", async (Guid machineId, HttpContext context,
            StandaloneAgentGatewayService gateway, CancellationToken ct) =>
            Results.Ok(await gateway.SendMachineCommandAsync(machineId,
                await AgentEnrollmentEndpoints.ReadRequest<RemoteCommand>(context), ct)));
        machines.MapPost("/updates", async (Guid machineId, HttpContext context,
            StandaloneAgentGatewayService gateway, CancellationToken ct) =>
        {
            await gateway.RequestMachineUpdateAsync(machineId,
                await AgentEnrollmentEndpoints.ReadRequest<AgentUpdatePackage>(context), ct);
            return Results.Accepted();
        });
        group.MapGet("/revoked", (HttpContext context) =>
        {
            if (context.RequestServices.GetService<AgentEnrollmentMode>()?.Enabled == true)
                return Results.Ok(context.RequestServices.GetRequiredService<AgentEnrollmentRegistry>().List()
                    .Where(e => e.State == AgentEnrollmentState.Revoked));
            return Results.Ok(context.RequestServices.GetRequiredService<IRevokedAgentStore>().GetAll());
        });
        group.MapPost("/{agentId}/revoke", RevokeAgent);
        group.MapDelete("/{agentId}/revoke", ReinstateAgent);
    }

    private static IResult GetConnectedAgents(StandaloneAgentGatewayService gateway)
        => Results.Ok(gateway.GetConnectedAgents());

    private static IResult GetAgentMetadata(string agentId, StandaloneAgentGatewayService gateway)
    {
        try
        {
            return Results.Ok(gateway.GetMetadata(agentId));
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IResult> SendCommand(
        string agentId,
        RemoteCommand command,
        HttpContext context,
        StandaloneAgentGatewayService gateway,
        CancellationToken ct)
    {
        if (command.CommandType.Equals("ManagedServices", StringComparison.OrdinalIgnoreCase) && !context.Request.IsHttps)
            return Results.Problem("Managed services require HTTPS.", statusCode: 403);
        try
        {
            return Results.Ok(await gateway.SendCommandAsync(agentId, command, ct));
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IResult> RequestUpdate(
        string agentId,
        AgentUpdatePackage package,
        StandaloneAgentGatewayService gateway,
        CancellationToken ct)
    {
        try
        {
            await gateway.RequestUpdateAsync(agentId, package, ct);
            return Results.Accepted();
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static IResult RevokeAgent(
        string agentId,
        RevokeRequest? request,
        HttpContext context,
        StandaloneAgentGatewayService gateway)
    {
        if (context.RequestServices.GetService<AgentEnrollmentMode>()?.Enabled == true)
        {
            if (request?.ExpectedVersion is not { } expected)
                return Results.Conflict(new { error = "Managed revocation requires the caller-confirmed enrollment version." });
            return Results.Ok(context.RequestServices.GetRequiredService<AgentEnrollmentRegistry>()
                .Transition(agentId, expected, AgentEnrollmentTransition.Revoke));
        }
        var store = context.RequestServices.GetRequiredService<IRevokedAgentStore>();
        if (!context.Request.IsHttps || !store.HasDurableStorage)
            return Results.Problem("Peering revocation requires HTTPS and durable storage.", statusCode: 403);
        var reason = request?.Reason ?? "Revoked via control plane API";
        gateway.RevokeAgent(agentId, reason);

        return Results.Ok(new { agentId, status = "revoked", reason });
    }

    private static IResult ReinstateAgent(string agentId, HttpContext context)
    {
        if (context.RequestServices.GetService<AgentEnrollmentMode>()?.Enabled == true)
            return Results.Conflict(new { error = "Managed reinstatement requires fresh credentials and a confirmed version." });
        var store = context.RequestServices.GetRequiredService<IRevokedAgentStore>();
        if (!context.Request.IsHttps || !store.HasDurableStorage)
            return Results.Problem("Re-pairing requires HTTPS and durable storage.", statusCode: 403);
        store.Reinstate(agentId);
        return Results.Ok(new { agentId, status = "reinstated" });
    }
}

internal sealed record RevokeRequest(string? Reason, long? ExpectedVersion = null);
