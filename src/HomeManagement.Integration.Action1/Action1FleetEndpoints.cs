using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Integration.Action1.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
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
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
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
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "listing fleet status", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
            }
        });

        group.MapGet("summary", async (
            Action1Client action1,
            IInventoryService inventory,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
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
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "building fleet patch summary", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
            }
        });

        group.MapGet("{machineId:guid}/patches", async (
            Guid machineId,
            Action1Client action1,
            IInventoryService inventory,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
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
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "fetching machine patches", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
            }
        });

        group.MapPost("{machineId:guid}/approve", async (
            Guid machineId,
            ApprovePatchesRequest request,
            Action1Client action1,
            IInventoryService inventory,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
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
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "approving patches for machine", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
            }
        });

        // ── Vulnerabilities (CVE correlation) ─────────────────────────────────

        group.MapGet("vulnerabilities", async (
            Action1Client action1,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                var vulns = await action1.GetVulnerabilitiesAsync(ct);
                return Results.Ok(vulns);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "fetching fleet vulnerabilities", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
            }
        });

        // ── Aggregate pending patches across all enrolled machines ─────────────
        // Fetches available patches for every machine that has missing-patch counts > 0,
        // fanning out with limited concurrency so the broker doesn't hammer Action1.
        group.MapGet("pending-patches", async (
            Action1Client action1,
            IInventoryService inventory,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                var machineQuery = new MachineQuery(IncludeDeleted: false, Page: 1, PageSize: 500);
                var machinesTask = inventory.QueryAsync(machineQuery, ct);
                var endpointsTask = action1.ListEndpointsAsync(ct);
                await Task.WhenAll(machinesTask, endpointsTask);

                var machines = machinesTask.Result;
                var action1Endpoints = endpointsTask.Result;

                var endpointById = action1Endpoints.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
                var endpointByName = new Dictionary<string, Action1Endpoint>(StringComparer.OrdinalIgnoreCase);
                foreach (var ep in action1Endpoints)
                {
                    endpointByName.TryAdd(ep.Name, ep);
                    var dot = ep.Name.IndexOf('.');
                    if (dot > 0) endpointByName.TryAdd(ep.Name[..dot], ep);
                }

                // Only fetch full patch lists for machines that are enrolled AND have pending counts.
                var candidates = machines.Items
                    .Select(m =>
                    {
                        Action1Endpoint? ep = null;
                        if (m.Tags.TryGetValue("action1:endpoint_id", out var tagId) && !string.IsNullOrEmpty(tagId))
                            endpointById.TryGetValue(tagId, out ep);
                        if (ep is null) endpointByName.TryGetValue(m.Hostname.Value, out ep);
                        if (ep is null && !string.IsNullOrEmpty(m.Fqdn)) endpointByName.TryGetValue(m.Fqdn, out ep);
                        return (Machine: m, Endpoint: ep);
                    })
                    .Where(x => x.Endpoint is not null &&
                                (x.Endpoint.MissingCriticalUpdates > 0 || x.Endpoint.MissingOtherUpdates > 0))
                    .ToList();

                // Fan-out with concurrency cap to stay under Action1 rate limits (30 req/min).
                using var semaphore = new SemaphoreSlim(5);
                var tasks = candidates.Select(async x =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var patches = await action1.GetAvailablePatchesAsync(x.Endpoint!.Id, ct);
                        return new MachinePendingPatchesDto(
                            MachineId: x.Machine.Id,
                            Hostname: x.Machine.Hostname.Value,
                            Action1EndpointId: x.Endpoint.Id,
                            OsType: x.Endpoint.OsType,
                            PatchRiskLevel: ComputePatchRiskLevel(x.Endpoint, null),
                            CriticalCount: x.Endpoint.MissingCriticalUpdates,
                            OtherCount: x.Endpoint.MissingOtherUpdates,
                            Patches: patches);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks);

                // Critical machines first, then by total pending count.
                var ordered = results
                    .Where(r => r.Patches.Count > 0)
                    .OrderByDescending(r => r.CriticalCount)
                    .ThenByDescending(r => r.OtherCount)
                    .ToList();

                return Results.Ok(ordered);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "loading pending patches", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
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
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
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
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "listing schedules", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
            }
        });

        // ── Trigger immediate sync of HM-managed schedules ────────────────────
        // POST /api/action1/schedules/sync
        group.MapPost("sync", async (
            Action1Client action1,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
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
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "syncing schedules", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
            }
        });

        // ── Update schedule settings (enable/disable/change timing) ───────────
        group.MapPatch("{scheduleId}", async (
            string scheduleId,
            SchedulePatchRequest request,
            Action1Client action1,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
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
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "updating schedule", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
            }
        });

        // ── Delete a schedule ─────────────────────────────────────────────────
        group.MapDelete("{scheduleId}", async (
            string scheduleId,
            Action1Client action1,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
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
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "deleting schedule", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
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

    // ── Clients / enrollment endpoints ────────────────────────────────────────

    public static IEndpointRouteBuilder MapAction1ClientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/action1/clients")
            .WithTags("Action1Clients")
            .RequireAuthorization();

        // GET /api/action1/clients
        // Returns all Action1 organizations (MSP clients) with their enrolled endpoints.
        // For single-org setups this returns one entry. For MSP setups, one per client.
        group.MapGet("", async (
            Action1Client action1,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Fleet");
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                // Fetch orgs + current org endpoints + groups in parallel
                var orgsTask = action1.GetOrganizationsAsync(ct);
                var endpointsTask = action1.ListEndpointsAsync(ct);
                var groupsTask = action1.GetEndpointGroupsAsync(ct);
                var schedulesTask = action1.GetSchedulesAsync(ct);
                await Task.WhenAll(orgsTask, endpointsTask, groupsTask, schedulesTask);

                var orgs = orgsTask.Result;
                var endpoints = endpointsTask.Result;
                var groups = groupsTask.Result;
                var schedules = schedulesTask.Result;

                // Determine the most common agent version as "current" baseline
                var currentAgentVersion = endpoints
                    .Where(e => !string.IsNullOrEmpty(e.AgentVersion))
                    .GroupBy(e => e.AgentVersion!)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key;

                // Build schedule → endpoint/group membership map
                // Schedules targeting ALL or an EndpointGroup apply to all/group endpoints
                // Schedules targeting specific Endpoints map directly
                var schedulesByEndpoint = new Dictionary<string, List<Models.Action1Schedule>>(StringComparer.OrdinalIgnoreCase);
                var broadSchedules = new List<Models.Action1Schedule>();

                foreach (var s in schedules)
                {
                    var targets = s.Endpoints ?? [];
                    var hasAll = targets.Any(e => string.Equals(e.Id, "ALL", StringComparison.OrdinalIgnoreCase));
                    if (hasAll || targets.Count == 0)
                    {
                        broadSchedules.Add(s);
                        continue;
                    }
                    foreach (var t in targets)
                    {
                        if (string.Equals(t.Type, "Endpoint", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!schedulesByEndpoint.TryGetValue(t.Id, out var list))
                                schedulesByEndpoint[t.Id] = list = [];
                            list.Add(s);
                        }
                        // EndpointGroup targets: resolved after group membership fetch (future)
                    }
                }

                // Map endpoints to client DTOs
                var endpointDtos = endpoints.Select(ep =>
                {
                    var epSchedules = broadSchedules.ToList();
                    if (schedulesByEndpoint.TryGetValue(ep.Id, out var direct))
                        epSchedules.AddRange(direct);

                    return new Action1EnrolledEndpointDto(
                        EndpointId: ep.Id,
                        Name: ep.Name,
                        AgentVersion: ep.AgentVersion,
                        IsAgentCurrent: currentAgentVersion is null ||
                            string.Equals(ep.AgentVersion, currentAgentVersion, StringComparison.OrdinalIgnoreCase),
                        Status: ep.Status,
                        IpAddress: ep.IpAddress,
                        ExternalAddress: ep.ExternalAddress,
                        OsName: ep.OsName,
                        OsType: ep.OsType,
                        LastLoggedInUser: ep.LastLoggedInUser,
                        LastSeenUtc: ep.LastSeenUtc,
                        MissingCriticalPatches: ep.MissingCriticalUpdates,
                        MissingOtherPatches: ep.MissingOtherUpdates,
                        Schedules: epSchedules
                            .DistinctBy(s => s.Id)
                            .Select(s => new Action1ClientScheduleDto(
                                ScheduleId: s.Id,
                                ScheduleName: s.Name,
                                IsManagedByHm: s.Name.StartsWith("homeManagement: ", StringComparison.OrdinalIgnoreCase),
                                Settings: s.Settings))
                            .ToList());
                }).ToList();

                // Map organizations — annotate which one matches the configured orgId
                var orgDtos = orgs.Select(org => new Action1OrgDto(
                    OrgId: org.Id,
                    Name: org.Name,
                    Description: org.Description,
                    IsConfiguredOrg: string.Equals(org.Id, opts.Value.OrganizationId, StringComparison.OrdinalIgnoreCase),
                    EndpointCount: org.EndpointCount,
                    Status: org.Status,
                    CreatedUtc: org.CreatedUtc,
                    // Endpoints and groups are only populated for the configured org
                    Endpoints: string.Equals(org.Id, opts.Value.OrganizationId, StringComparison.OrdinalIgnoreCase)
                        ? endpointDtos : [],
                    Groups: string.Equals(org.Id, opts.Value.OrganizationId, StringComparison.OrdinalIgnoreCase)
                        ? groups.Select(g => new Action1GroupDto(g.Id, g.Name, g.Description, g.EndpointCount)).ToList()
                        : [])).ToList();

                return Results.Ok(orgDtos);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "listing Action1 clients", ex.Message);
                return Results.Problem("Action1 API request failed. Check broker logs.", statusCode: 502, title: "Action1 API Error");
            }
        });

        return app;
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

/// <summary>
/// An Action1 organization (MSP client / isolated tenant) with its enrolled endpoints.
/// </summary>
public sealed record Action1OrgDto(
    string OrgId,
    string Name,
    string? Description,
    bool IsConfiguredOrg,
    int EndpointCount,
    string? Status,
    DateTime? CreatedUtc,
    IReadOnlyList<Action1EnrolledEndpointDto> Endpoints,
    IReadOnlyList<Action1GroupDto> Groups);

/// <summary>An endpoint group within an org.</summary>
public sealed record Action1GroupDto(
    string GroupId,
    string Name,
    string? Description,
    int EndpointCount);

/// <summary>
/// A single Action1-enrolled endpoint with agent details and cross-referenced schedules.
/// </summary>
public sealed record Action1EnrolledEndpointDto(
    string EndpointId,
    string Name,
    string? AgentVersion,
    bool IsAgentCurrent,
    string Status,
    string? IpAddress,
    string? ExternalAddress,
    string? OsName,
    string? OsType,
    string? LastLoggedInUser,
    DateTime? LastSeenUtc,
    int MissingCriticalPatches,
    int MissingOtherPatches,
    IReadOnlyList<Action1ClientScheduleDto> Schedules);

/// <summary>A schedule that targets this client endpoint (or ALL endpoints).</summary>
public sealed record Action1ClientScheduleDto(
    string ScheduleId,
    string ScheduleName,
    bool IsManagedByHm,
    string? Settings);

/// <summary>
/// Aggregate pending-patch view for one fleet machine.
/// Returned by GET /api/action1/fleet/pending-patches.
/// </summary>
public sealed record MachinePendingPatchesDto(
    Guid MachineId,
    string Hostname,
    string? Action1EndpointId,
    string? OsType,
    string PatchRiskLevel,
    int CriticalCount,
    int OtherCount,
    IReadOnlyList<Models.Action1Patch> Patches);
