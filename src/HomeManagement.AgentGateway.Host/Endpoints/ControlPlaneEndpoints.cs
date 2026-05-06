using System.Security.Cryptography;
using HomeManagement.Abstractions.Models;
using HomeManagement.AgentGateway.Host.Services;

namespace HomeManagement.AgentGateway.Host.Endpoints;

public static class ControlPlaneEndpoints
{
    private const string HeaderName = "x-agent-gateway-api-key";

    public static void MapControlPlaneEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/agents");

        group.AddEndpointFilter(async (context, next) =>
        {
            var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var expectedKey = configuration["AgentGateway:ApiKey"]
                ?? throw new InvalidOperationException("AgentGateway:ApiKey must be configured.");

            var suppliedKey = context.HttpContext.Request.Headers[HeaderName].FirstOrDefault();
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expectedKey ?? string.Empty);
            var suppliedBytes = System.Text.Encoding.UTF8.GetBytes(suppliedKey ?? string.Empty);
            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
            {
                return Results.Unauthorized();
            }

            return await next(context);
        });

        group.MapGet("/", (StandaloneAgentGatewayService gateway) => Results.Ok(gateway.GetConnectedAgents()));

        group.MapGet("/{agentId}", (string agentId, StandaloneAgentGatewayService gateway) =>
        {
            try
            {
                return Results.Ok(gateway.GetMetadata(agentId));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapPost("/{agentId}/commands", async (
            string agentId,
            RemoteCommand command,
            StandaloneAgentGatewayService gateway,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await gateway.SendCommandAsync(agentId, command, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapPost("/{agentId}/updates", async (
            string agentId,
            AgentUpdatePackage package,
            StandaloneAgentGatewayService gateway,
            CancellationToken ct) =>
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
        });

        // ── Agent revocation management ─────────────────────────────────────────
        // GET    /internal/agents/revoked             — list all revoked agents
        // POST   /internal/agents/{agentId}/revoke    — block the agent immediately
        // DELETE /internal/agents/{agentId}/revoke    — reinstate the agent
        group.MapGet("/revoked", (IRevokedAgentStore store) =>
            Results.Ok(store.GetAll()));

        group.MapPost("/{agentId}/revoke", (
            string agentId,
            RevokeRequest? request,
            IRevokedAgentStore store,
            StandaloneAgentGatewayService gateway) =>
        {
            var reason = request?.Reason ?? "Revoked via control plane API";
            store.Revoke(agentId, reason);

            // Forcibly disconnect if currently online
            if (gateway.GetConnectedAgents().Any(a => string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase)))
            {
                gateway.UnregisterAgent(agentId);
            }

            return Results.Ok(new { agentId, status = "revoked", reason });
        });

        group.MapDelete("/{agentId}/revoke", (string agentId, IRevokedAgentStore store) =>
        {
            store.Reinstate(agentId);
            return Results.Ok(new { agentId, status = "reinstated" });
        });
    }
}

internal sealed record RevokeRequest(string? Reason);
