using HomeManagement.Integration.Action1.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// Broker REST endpoints for managing the Action1 org-level update catalog.
///
/// Routes:
///   GET  /api/action1/catalog          — List catalog updates (filtered by approval_status)
///   POST /api/action1/catalog/approve  — Bulk set approval status on selected update IDs
///
/// Background:
///   Action1 maintains an org-level "update catalog" — a list of discovered updates
///   with approval_status = New | Approved | Declined.  This is separate from the
///   per-endpoint missing-updates list.  Automations with update_approval="auto" can
///   bypass this gate, but changes here are visible in the Action1 console's
///   Update Approval screen and provide a clean audit trail.
/// </summary>
public static class Action1CatalogEndpoints
{
    public static IEndpointRouteBuilder MapAction1CatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/action1/catalog")
            .WithTags("Action1Catalog")
            .RequireAuthorization();

        // ── List catalog updates ───────────────────────────────────────────────
        // GET /api/action1/catalog?approvalStatus=New
        group.MapGet("", async (
            Action1Client action1,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            string approvalStatus = "New",
            CancellationToken ct = default) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Catalog");
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            var validStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Approved", "Declined", "New" };
            if (!validStatuses.Contains(approvalStatus))
                return Results.BadRequest(new { Message = $"Invalid approvalStatus '{approvalStatus}'. Must be Approved, Declined, or New." });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            logger.LogInformation("CatalogFetch started: approvalStatus={Status}", approvalStatus);
            try
            {
                var updates = await action1.GetCatalogUpdatesAsync(approvalStatus, ct);
                sw.Stop();
                logger.LogInformation("CatalogFetch completed: {Count} updates in {ElapsedMs}ms, approvalStatus={Status}",
                    updates.Count, sw.ElapsedMilliseconds, approvalStatus);
                return Results.Ok(updates.Select(MapCatalogDto).ToList());
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogError(ex, "CatalogFetch failed after {ElapsedMs}ms: {Error}", sw.ElapsedMilliseconds, ex.Message);
                return Results.Problem("Action1 catalog fetch failed. Check broker logs for details.", statusCode: 502, title: "Action1 API Error");
            }
        });

        // ── Diagnostic test endpoint ──────────────────────────────────────────────
        // GET /api/action1/catalog/test
        // Returns timing info and error details. Always produces a JSON body. No auth bypass.
        group.MapGet("test", async (
            Action1Client action1,
            IOptions<Action1Options> opts,
            CancellationToken ct = default) =>
        {
            if (!opts.Value.Enabled)
                return Results.Ok(new { success = false, enabled = false, itemCount = 0, elapsedMs = 0, error = "Action1 integration is not enabled (Action1__Enabled=false in config)" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var updates = await action1.GetCatalogUpdatesAsync("New", cts.Token);
                sw.Stop();
                return Results.Ok(new { success = true, enabled = true, itemCount = updates.Count, elapsedMs = sw.ElapsedMilliseconds, error = (string?)null });
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return Results.Ok(new { success = false, enabled = true, itemCount = 0, elapsedMs = sw.ElapsedMilliseconds, error = $"Action1 API did not respond within 20s (elapsed: {sw.ElapsedMilliseconds}ms)" });
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Results.Ok(new { success = false, enabled = true, itemCount = 0, elapsedMs = sw.ElapsedMilliseconds, error = ex.Message });
            }
        });

        // ── Probe approval endpoint ──────────────────────────────────────────────
        // POST /api/action1/catalog/probe-approve
        // Body: { "updateId": "<id>", "approvalStatus": "Approved", "scope": "Organization" }
        // Calls SetCatalogApprovalAsync for a single update and returns detailed status.
        // Use this to test the actual Action1 API response without triggering bulk approval.
        group.MapPost("probe-approve", async (
            ProbeApproveRequest request,
            Action1Client action1,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct = default) =>
        {
            if (!opts.Value.Enabled)
                return Results.Ok(new { success = false, error = "Action1 not enabled" });

            var validScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Organization", "Enterprise" };
            if (!validScopes.Contains(request.Scope))
                return Results.BadRequest(new { error = $"Invalid scope '{request.Scope}'. Must be Organization or Enterprise." });

            var validStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Approved", "Declined", "New" };
            if (!validStatuses.Contains(request.ApprovalStatus))
                return Results.BadRequest(new { error = $"Invalid approvalStatus '{request.ApprovalStatus}'. Must be Approved, Declined, or New." });

            var logger = loggerFactory.CreateLogger("Broker.Action1.ProbeApprove");
            logger.LogInformation("Probe: PATCH approval test for updateId={Id} status={Status} scope={Scope}",
                request.UpdateId, request.ApprovalStatus, request.Scope);

            var ok = await action1.SetCatalogApprovalAsync(request.UpdateId, request.ApprovalStatus, request.Scope, ct);
            return Results.Ok(new { success = ok, updateId = request.UpdateId, approvalStatus = request.ApprovalStatus, scope = request.Scope });
        });

        // ── Bulk approve / decline catalog updates ────────────────────────────
        // POST /api/action1/catalog/approve
        // Body: { UpdateIds: ["id1","id2"], ApprovalStatus: "Approved" }
        group.MapPost("approve", async (
            CatalogApproveRequest request,
            Action1Client action1,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Broker.Action1.Catalog");
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            if (request.UpdateIds is null || request.UpdateIds.Count == 0)
                return Results.BadRequest(new { Message = "At least one update ID is required." });

            var validStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Approved", "Declined", "New" };

            if (!validStatuses.Contains(request.ApprovalStatus))
                return Results.BadRequest(new
                {
                    Message = $"Invalid ApprovalStatus '{request.ApprovalStatus}'. Must be Approved, Declined, or New."
                });

            try
            {
                // Fan out with limited concurrency — stay under Action1 rate limit.
                using var semaphore = new SemaphoreSlim(5);
                var tasks = request.UpdateIds.Select(async id =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var ok = await action1.SetCatalogApprovalAsync(id, request.ApprovalStatus, request.Scope, ct);
                        return (Id: id, Success: ok);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks);
                var succeeded = results.Where(r => r.Success).Select(r => r.Id).ToList();
                var failed = results.Where(r => !r.Success).Select(r => r.Id).ToList();

                return Results.Ok(new
                {
                    Approved = succeeded.Count,
                    Failed = failed.Count,
                    FailedIds = failed
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Action1: {Operation} failed: {Error}", "bulk catalog approval", ex.Message);
                return Results.Problem("Action1 approval request failed. Check broker logs for details.", statusCode: 502, title: "Action1 API Error");
            }
        });

        return app;
    }

    private static CatalogUpdateDto MapCatalogDto(Action1CatalogUpdate u) => new(
        Id: u.Id,
        Name: u.Name,
        Version: u.Version,
        Description: u.Description,
        Severity: u.Severity,
        Category: u.Category,
        UpdateType: u.UpdateType,
        ApprovalStatus: u.ApprovalStatus,
        RequiresReboot: u.RequiresReboot,
        PublishedUtc: u.PublishedUtc,
        KbArticleId: u.KbArticleId);
}

/// <summary>Single-update approval probe request for endpoint testing.</summary>
public sealed record ProbeApproveRequest(string UpdateId, string ApprovalStatus = "Approved", string Scope = "Organization");

/// <summary>Bulk catalog approval/decline request.</summary>
public sealed record CatalogApproveRequest(
    IReadOnlyList<string> UpdateIds,
    string ApprovalStatus = "Approved",
    string Scope = "Organization");

/// <summary>Broker-side DTO for a catalog update item.</summary>
public sealed record CatalogUpdateDto(
    string Id,
    string Name,
    string? Version,
    string? Description,
    string? Severity,
    string? Category,
    string? UpdateType,
    string? ApprovalStatus,
    bool RequiresReboot,
    DateTime? PublishedUtc,
    string? KbArticleId);
