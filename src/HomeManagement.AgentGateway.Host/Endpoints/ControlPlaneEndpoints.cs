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
            if (!string.Equals(expectedKey, suppliedKey, StringComparison.Ordinal))
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
    }
}