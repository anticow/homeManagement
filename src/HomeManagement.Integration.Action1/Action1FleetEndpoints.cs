using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Integration.Action1.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// Fleet-level patch management endpoints that correlate homeManagement machine inventory
/// with Action1 endpoint status, providing a single pane of glass for patch approvals.
///
/// Routes:
///   GET  /api/action1/fleet          — All HM machines with Action1 patch status
///   GET  /api/action1/fleet/summary  — Aggregate fleet patch health
///   GET  /api/action1/fleet/{machineId}/patches  — Available patches for a specific HM machine
///   POST /api/action1/fleet/{machineId}/approve  — Deploy/approve patches for a machine
/// </summary>
public static class Action1FleetEndpoints
{
    public static IEndpointRouteBuilder MapAction1FleetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/action1/fleet")
            .WithTags("Action1Fleet")
            .RequireAuthorization();

        group.MapGet("", async (
            Action1Client action1,
            IInventoryService inventory,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                var machineQuery = new MachineQuery(IncludeDeleted: false, Page: 1, PageSize: 500);
                var machines = await inventory.QueryAsync(machineQuery, ct);
                var action1Endpoints = await action1.ListEndpointsAsync(ct);

                var endpointById = action1Endpoints.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

                // Build lookup by ALL possible name forms so FQDN vs short-name mismatches resolve.
                // Action1 may register as "DC2.cowgomu.net" while HM stores "DC2", or vice versa.
                var endpointByName = new Dictionary<string, Action1Endpoint>(StringComparer.OrdinalIgnoreCase);
                foreach (var ep in action1Endpoints)
                {
                    endpointByName.TryAdd(ep.Name, ep);
                    // Also index the short name (first label of FQDN) if the name contains dots
                    var dot = ep.Name.IndexOf('.');
                    if (dot > 0)
                        endpointByName.TryAdd(ep.Name[..dot], ep);
                }

                var results = machines.Items.Select(machine =>
                {
                    Action1Endpoint? endpoint = null;

                    // 1. Prefer explicit tag binding — zero ambiguity
                    if (machine.Tags.TryGetValue("action1:endpoint_id", out var taggedId) &&
                        !string.IsNullOrEmpty(taggedId))
                    {
                        endpointById.TryGetValue(taggedId, out endpoint);
                    }

                    // 2. Fuzzy hostname match: try short name, then FQDN
                    if (endpoint is null)
                        endpointByName.TryGetValue(machine.Hostname.Value, out endpoint);

                    if (endpoint is null && !string.IsNullOrEmpty(machine.Fqdn))
                        endpointByName.TryGetValue(machine.Fqdn, out endpoint);

                    return new FleetMachineStatus(
                        MachineId: machine.Id,
                        Hostname: machine.Hostname.Value,
                        OsType: machine.OsType.ToString(),
                        MachineState: machine.State.ToString(),
                        Action1EndpointId: endpoint?.Id,
                        Action1Status: endpoint?.Status ?? "NotEnrolled",
                        Action1LastSeen: endpoint?.LastSeenUtc,
                        AgentVersion: endpoint?.AgentVersion,
                        CriticalPatchCount: endpoint?.MissingCriticalUpdates ?? 0,
                        OtherPatchCount: endpoint?.MissingOtherUpdates ?? 0,
                        LastLoggedInUser: endpoint?.LastLoggedInUser);
                }).ToList();

                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
            }
        });

        group.MapGet("summary", async (
            Action1Client action1,
            IInventoryService inventory,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                var endpoints = await action1.ListEndpointsAsync(ct);
                var machineQuery = new MachineQuery(IncludeDeleted: false, Page: 1, PageSize: 1);
                var machinePage = await inventory.QueryAsync(machineQuery, ct);

                var summary = new FleetPatchSummary(
                    TotalMachines: machinePage.TotalCount,
                    EnrolledInAction1: endpoints.Count,
                    TotalCriticalPatches: endpoints.Sum(e => e.MissingCriticalUpdates),
                    TotalOtherPatches: endpoints.Sum(e => e.MissingOtherUpdates),
                    FullyPatched: endpoints.Count(e =>
                        e.MissingCriticalUpdates == 0 && e.MissingOtherUpdates == 0),
                    Online: endpoints.Count(e =>
                        string.Equals(e.Status, "Online", StringComparison.OrdinalIgnoreCase)));

                return Results.Ok(summary);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
            }
        });

        group.MapGet("{machineId:guid}/patches", async (
            Guid machineId,
            Action1Client action1,
            IInventoryService inventory,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                var endpointId = await ResolveEndpointIdAsync(machineId, action1, inventory, ct);
                if (endpointId is null)
                    return Results.NotFound(new { Message = $"Machine {machineId} is not enrolled in Action1." });

                var patches = await action1.GetAvailablePatchesAsync(endpointId, ct);
                return Results.Ok(patches);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
            }
        });

        group.MapPost("{machineId:guid}/approve", async (
            Guid machineId,
            ApprovePatchesRequest request,
            Action1Client action1,
            IInventoryService inventory,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            if (request.PatchIds is null || request.PatchIds.Count == 0)
                return Results.BadRequest(new { Message = "At least one patch ID is required." });

            try
            {
                var endpointId = await ResolveEndpointIdAsync(machineId, action1, inventory, ct);
                if (endpointId is null)
                    return Results.NotFound(new { Message = $"Machine {machineId} is not enrolled in Action1." });

                var deploymentId = await action1.CreateDeploymentAsync(
                    endpointId, request.PatchIds, request.AllowReboot, ct);

                if (deploymentId is null)
                    return Results.Problem(
                        "Action1 failed to create the deployment. Check Action1 API access and endpoint ID.",
                        statusCode: 502);

                return Results.Accepted(
                    $"/api/action1/fleet/{machineId}/deployments/{deploymentId}",
                    new { DeploymentId = deploymentId, EndpointId = endpointId });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
            }
        });

        return app;
    }

    private static async Task<string?> ResolveEndpointIdAsync(
        Guid machineId,
        Action1Client action1,
        IInventoryService inventory,
        CancellationToken ct)
    {
        var machine = await inventory.GetAsync(machineId, ct);
        if (machine is null) return null;

        if (machine.Tags.TryGetValue("action1:endpoint_id", out var taggedId) &&
            !string.IsNullOrEmpty(taggedId))
            return taggedId;

        var endpoints = await action1.ListEndpointsAsync(ct);
        return endpoints
            .FirstOrDefault(e =>
                string.Equals(e.Name, machine.Hostname.Value, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }
}

public sealed record FleetMachineStatus(
    Guid MachineId,
    string Hostname,
    string OsType,
    string MachineState,
    string? Action1EndpointId,
    string Action1Status,
    DateTime? Action1LastSeen,
    string? AgentVersion,
    int CriticalPatchCount,
    int OtherPatchCount,
    string? LastLoggedInUser);

public sealed record FleetPatchSummary(
    int TotalMachines,
    int EnrolledInAction1,
    int TotalCriticalPatches,
    int TotalOtherPatches,
    int FullyPatched,
    int Online);

public sealed record ApprovePatchesRequest(
    IReadOnlyList<string> PatchIds,
    bool AllowReboot = false);
