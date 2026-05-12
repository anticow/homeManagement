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
                var machinesTask = inventory.QueryAsync(machineQuery, ct);
                var endpointsTask = action1.ListEndpointsAsync(ct);
                var lastPatchedTask = action1.GetLastPatchedDatesAsync(ct);
                await Task.WhenAll(machinesTask, endpointsTask, lastPatchedTask);

                var machines = machinesTask.Result;
                var action1Endpoints = endpointsTask.Result;
                var lastPatchedDates = lastPatchedTask.Result;

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
                        LastLoggedInUser: endpoint?.LastLoggedInUser,
                        LastPatchedUtc: endpoint?.Id is not null &&
                                        lastPatchedDates.TryGetValue(endpoint.Id, out var lp) ? lp : null,
                        PatchRiskLevel: ComputePatchRiskLevel(
                            endpoint,
                            endpoint?.Id is not null &&
                            lastPatchedDates.TryGetValue(endpoint.Id, out var lpRisk) ? lpRisk : null));
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
                var endpointsTask = action1.ListEndpointsAsync(ct);
                var lastPatchedTask = action1.GetLastPatchedDatesAsync(ct);
                var machineCountTask = inventory.QueryAsync(
                    new MachineQuery(IncludeDeleted: false, Page: 1, PageSize: 1), ct);
                await Task.WhenAll(endpointsTask, lastPatchedTask, machineCountTask);

                var endpoints = endpointsTask.Result;
                var lastPatchedDates = lastPatchedTask.Result;
                var machinePage = machineCountTask.Result;

                var now = DateTime.UtcNow;
                var enrolledWithHistory = endpoints
                    .Select(e => (Endpoint: e,
                                  LastPatched: lastPatchedDates.TryGetValue(e.Id, out var lp) ? lp : (DateTime?)null))
                    .ToList();

                var summary = new FleetPatchSummary(
                    TotalMachines: machinePage.TotalCount,
                    EnrolledInAction1: endpoints.Count,
                    TotalCriticalPatches: endpoints.Sum(e => e.MissingCriticalUpdates),
                    TotalOtherPatches: endpoints.Sum(e => e.MissingOtherUpdates),
                    FullyPatched: endpoints.Count(e =>
                        e.MissingCriticalUpdates == 0 && e.MissingOtherUpdates == 0),
                    Online: endpoints.Count(e =>
                        string.Equals(e.Status, "Online", StringComparison.OrdinalIgnoreCase)),
                    PatchedWithin30Days: enrolledWithHistory.Count(x =>
                        x.LastPatched.HasValue && (now - x.LastPatched.Value).TotalDays <= 30),
                    PatchedWithin90Days: enrolledWithHistory.Count(x =>
                        x.LastPatched.HasValue && (now - x.LastPatched.Value).TotalDays is > 30 and <= 90),
                    OverdueCount: enrolledWithHistory.Count(x =>
                        !x.LastPatched.HasValue || (now - x.LastPatched.Value).TotalDays > 90));

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

            if (request.Patches is null || request.Patches.Count == 0)
                return Results.BadRequest(new { Message = "At least one patch is required." });

            try
            {
                var endpointId = await ResolveEndpointIdAsync(machineId, action1, inventory, ct);
                if (endpointId is null)
                    return Results.NotFound(new { Message = $"Machine {machineId} is not enrolled in Action1." });

                var patches = request.Patches
                    .Select(p => new PatchToInstall(p.Id, p.Version))
                    .ToList();

                var deploymentId = await action1.CreateDeploymentAsync(
                    endpointId, patches, request.AllowReboot, ct);

                if (deploymentId is null)
                    return Results.Problem(
                        "Action1 failed to create the deployment policy. Check Action1 API access and endpoint ID.",
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

        // ── Vulnerabilities (CVE correlation) ─────────────────────────────────

        group.MapGet("vulnerabilities", async (
            Action1Client action1,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                var vulns = await action1.GetVulnerabilitiesAsync(ct);
                return Results.Ok(vulns);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
            }
        });

        return app;
    }

    // ── Public schedule management entry point ────────────────────────────────
    // Registered separately under /api/action1/schedules to keep concerns clean.

    public static IEndpointRouteBuilder MapAction1ScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/action1/schedules")
            .WithTags("Action1Schedules")
            .RequireAuthorization();

        // ── List all schedules ────────────────────────────────────────────────
        group.MapGet("", async (
            Action1Client action1,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                var schedules = await action1.GetSchedulesAsync(ct);
                var syncConfigured = opts.Value.ScheduleSync.Enabled && opts.Value.ScheduleSync.Rules.Count > 0;
                return Results.Ok(new
                {
                    SyncConfigured = syncConfigured,
                    RuleCount = opts.Value.ScheduleSync.Rules.Count,
                    Schedules = schedules.Select(MapScheduleDto).ToList()
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
            }
        });

        // ── Trigger immediate sync of HM-managed schedules ────────────────────
        // POST /api/action1/schedules/sync
        group.MapPost("sync", async (
            Action1Client action1,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            if (!opts.Value.ScheduleSync.Enabled || opts.Value.ScheduleSync.Rules.Count == 0)
                return Results.Problem(
                    "Schedule sync is not configured. Set Action1:ScheduleSync:Enabled=true and add Rules.",
                    statusCode: 400);

            try
            {
                var existing = await action1.GetSchedulesAsync(ct);
                var existingByName = existing.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

                var created = new List<string>();
                var updated = new List<string>();

                foreach (var rule in opts.Value.ScheduleSync.Rules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Name)) continue;

                    existingByName.TryGetValue(rule.FullName, out var found);
                    var body = Action1ScheduleSyncService.BuildScheduleBody(rule);

                    if (found is null)
                    {
                        var id = await action1.CreateScheduleAsync(body, ct);
                        if (id is not null) created.Add(rule.FullName);
                    }
                    else
                    {
                        await action1.UpdateScheduleAsync(found.Id, body, ct);
                        updated.Add(rule.FullName);
                    }
                }

                return Results.Ok(new { Created = created, Updated = updated });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
            }
        });

        // ── Update schedule settings (enable/disable/change timing) ───────────
        group.MapPatch("{scheduleId}", async (
            string scheduleId,
            SchedulePatchRequest request,
            Action1Client action1,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                var patch = new Dictionary<string, object?>();
                if (request.Settings is not null) patch["settings"] = request.Settings;
                if (request.Name is not null) patch["name"] = request.Name;

                if (patch.Count == 0)
                    return Results.BadRequest(new { Message = "No fields to update." });

                var ok = await action1.UpdateScheduleAsync(scheduleId, patch, ct);
                return ok ? Results.Ok() : Results.Problem("Update failed", statusCode: 502);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
            }
        });

        // ── Delete a schedule ─────────────────────────────────────────────────
        group.MapDelete("{scheduleId}", async (
            string scheduleId,
            Action1Client action1,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                // Safety guard: only allow deleting HM-managed schedules from this endpoint
                var schedules = await action1.GetSchedulesAsync(ct);
                var target = schedules.FirstOrDefault(s =>
                    string.Equals(s.Id, scheduleId, StringComparison.OrdinalIgnoreCase));

                if (target is null)
                    return Results.NotFound(new { Message = $"Schedule {scheduleId} not found." });

                if (!target.Name.StartsWith("homeManagement: ", StringComparison.OrdinalIgnoreCase))
                    return Results.Problem(
                        "Only homeManagement-managed schedules (name prefix 'homeManagement: ') can be deleted via this API.",
                        statusCode: 403);

                var ok = await action1.DeleteScheduleAsync(scheduleId, ct);
                return ok ? Results.NoContent() : Results.Problem("Delete failed", statusCode: 502);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
            }
        });

        return app;
    }

    private static ScheduleDto MapScheduleDto(Models.Action1Schedule s)
    {
        var ap = s.Actions is { Count: > 0 } acts ? acts[0].Params : null;
        return new ScheduleDto(
            Id: s.Id,
            Name: s.Name,
            Settings: s.Settings,
            RetryMinutes: s.RetryMinutes,
            LastRun: s.LastRun,
            NextRun: s.NextRun,
            IsSystem: s.IsSystem,
            IsManagedByHm: s.Name.StartsWith("homeManagement: ", StringComparison.OrdinalIgnoreCase),
            UpdateApproval: ap?.UpdateApproval,
            DeferDays: ap?.AutomaticApprovalDelayDays ?? 0,
            AllowReboot: string.Equals(ap?.RebootOptions?.AutoReboot, "yes", StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Derives a patch risk level string for UI color-coding.
    ///
    /// Thresholds:
    ///   Healthy  — no pending patches AND last patched ≤ 30 days ago
    ///   Warning  — other-only patches pending OR last patched 31–90 days ago
    ///   Overdue  — critical patches pending OR last patched > 90 days OR no patch history
    ///   Unknown  — not enrolled in Action1
    /// </summary>
    private static string ComputePatchRiskLevel(Action1Endpoint? endpoint, DateTime? lastPatchedUtc)
    {
        if (endpoint is null)
            return "Unknown";

        var days = lastPatchedUtc.HasValue
            ? (DateTime.UtcNow - lastPatchedUtc.Value).TotalDays
            : (double?)null;

        if (endpoint.MissingCriticalUpdates > 0 || days is null or > 90)
            return "Overdue";

        if (endpoint.MissingOtherUpdates > 0 || days > 30)
            return "Warning";

        return "Healthy";
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
    string? LastLoggedInUser,
    DateTime? LastPatchedUtc,
    string PatchRiskLevel);

public sealed record FleetPatchSummary(
    int TotalMachines,
    int EnrolledInAction1,
    int TotalCriticalPatches,
    int TotalOtherPatches,
    int FullyPatched,
    int Online,
    int PatchedWithin30Days,
    int PatchedWithin90Days,
    int OverdueCount);

public sealed record ApprovePatchesRequest(
    IReadOnlyList<PatchApprovalItem> Patches,
    bool AllowReboot = false);

/// <summary>A patch ID + version pair for patch approval requests.</summary>
public sealed record PatchApprovalItem(string Id, string? Version);

/// <summary>DTO for a single Action1 automation schedule as returned by the broker.</summary>
public sealed record ScheduleDto(
    string Id,
    string Name,
    string? Settings,
    string? RetryMinutes,
    DateTime? LastRun,
    DateTime? NextRun,
    bool IsSystem,
    bool IsManagedByHm,
    string? UpdateApproval,
    int DeferDays,
    bool AllowReboot);

/// <summary>Partial update request for a schedule (enable/disable/rename).</summary>
public sealed record SchedulePatchRequest(
    string? Settings,
    string? Name);
