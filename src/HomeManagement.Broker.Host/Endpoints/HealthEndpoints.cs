using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Broker.Host.Endpoints;

/// <summary>
/// Agent health monitoring endpoints.
/// Exposes aggregated health summaries (CPU, memory, disk) for managed agents.
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/agents/health")
            .WithTags("Health")
            .RequireAuthorization();

        group.MapGet("/", async (
            IInventoryService inventory,
            IAgentHealthService health,
            CancellationToken ct) =>
        {
            // Query all machines
            var query = new MachineQuery { PageSize = 1000 };
            var machinesPage = await inventory.QueryAsync(query, ct);

            if (machinesPage.Items.Count == 0)
                return Results.Ok(new List<AgentHealthSummary>());

            // Convert machines to health query tuples
            var machines = machinesPage.Items
                .Select(m => (m.Id, m.Hostname.Value, m.OsType.ToString()))
                .ToList();

            // Query health for all machines in parallel
            var healthSummaries = await health.GetAllAgentHealthAsync(machines, ct);

            return Results.Ok(healthSummaries);
        })
        .WithName("GetAllAgentHealth")
        .WithDescription("Get aggregated health summaries for all agents (CPU, memory, disk usage).");

        group.MapGet("/{machineId:guid}", async (
            Guid machineId,
            IInventoryService inventory,
            IAgentHealthService health,
            CancellationToken ct) =>
        {
            // Fetch machine
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null)
                return Results.NotFound(new { error = "Machine not found" });

            // Query health for single machine
            var summary = await health.GetAgentHealthAsync(
                machine.Id,
                machine.Hostname.Value,
                machine.OsType.ToString(),
                ct);

            return Results.Ok(summary);
        })
        .WithName("GetAgentHealth")
        .WithDescription("Get health summary for a specific agent (CPU, memory, disk usage).");
    }
}
