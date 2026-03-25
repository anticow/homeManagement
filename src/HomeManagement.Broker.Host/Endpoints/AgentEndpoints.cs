using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Broker.Host.Endpoints;

/// <summary>
/// Agent presence and proxy endpoints exposed through the Broker boundary.
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/agents")
            .WithTags("Agents")
            .RequireAuthorization();

        group.MapGet("/", (IAgentGateway gateway) => Results.Ok(gateway.GetConnectedAgents()));

        group.MapGet("/{agentId}", async (string agentId, IAgentGateway gateway, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await gateway.GetMetadataAsync(agentId, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        group.MapPost("/{agentId}/commands", async (
            string agentId,
            RemoteCommand command,
            IAgentGateway gateway,
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
            IAgentGateway gateway,
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
    }
}