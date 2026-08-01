using System.Security.Cryptography;
using HomeManagement.Abstractions.Models;
using HomeManagement.AgentGateway.Host.Services;

namespace HomeManagement.AgentGateway.Host.Endpoints;

public static class ControlPlaneEndpoints
{
    private const string HeaderName = "x-agent-gateway-api-key";

    public static void MapControlPlaneEndpoints(this WebApplication app)
        {
            var group = app.MapGroup("/internal/agents")
                .WithMetadata(new ApiKeyValidationRequired());

            group.MapGet("/", GetConnectedAgents);
            group.MapGet("/{agentId}", GetAgentMetadata);
            group.MapPost("/{agentId}/commands", SendCommand);
            group.MapPost("/{agentId}/updates", RequestUpdate);
            group.MapGet("/revoked", GetRevokedAgents);
            group.MapPost("/{agentId}/revoke", RevokeAgent);
            group.MapDelete("/{agentId}/revoke", ReinstateAgent);
        }

        // Mark endpoints that require validation
        private sealed class ApiKeyValidationRequired;

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
            StandaloneAgentGatewayService gateway,
            CancellationToken ct)
        {
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

        private static IResult GetRevokedAgents(IRevokedAgentStore store)
            => Results.Ok(store.GetAll());

        private static IResult RevokeAgent(
            string agentId,
            RevokeRequest? request,
            IRevokedAgentStore store,
            StandaloneAgentGatewayService gateway)
        {
            var reason = request?.Reason ?? "Revoked via control plane API";
            store.Revoke(agentId, reason);

            if (gateway.GetConnectedAgents().Any(a => string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase)))
            {
                gateway.UnregisterAgent(agentId);
            }

            return Results.Ok(new { agentId, status = "revoked", reason });
        }

        private static IResult ReinstateAgent(string agentId, IRevokedAgentStore store)
        {
            store.Reinstate(agentId);
            return Results.Ok(new { agentId, status = "reinstated" });
        }
}

internal sealed record RevokeRequest(string? Reason);
