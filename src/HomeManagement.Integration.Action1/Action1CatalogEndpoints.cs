using HomeManagement.Integration.Action1.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// Broker REST endpoints for managing the Action1 org-level update catalog.
///
/// Routes:
///   GET  /api/action1/catalog                    — List catalog updates (filtered by approval_status)
///   POST /api/action1/catalog/approve             — Start a background bulk-approval job; returns { jobId }
///   GET  /api/action1/catalog/approve/{jobId}     — Poll job progress
///   POST /api/action1/catalog/probe-approve       — Single-update test endpoint
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
    /// <summary>Register the ApprovalJobStore singleton. Call from Program.cs / DI setup.</summary>
    public static IServiceCollection AddApprovalJobStore(this IServiceCollection services) =>
        services.AddSingleton<ApprovalJobStore>();

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
            var resolvedScope = request.Scope ?? opts.Value.ApprovalScope;
            if (!validScopes.Contains(resolvedScope))
                return Results.BadRequest(new { error = $"Invalid scope '{resolvedScope}'. Must be Organization or Enterprise." });

            var validStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Approved", "Declined", "New" };
            if (!validStatuses.Contains(request.ApprovalStatus))
                return Results.BadRequest(new { error = $"Invalid approvalStatus '{request.ApprovalStatus}'. Must be Approved, Declined, or New." });

            var logger = loggerFactory.CreateLogger("Broker.Action1.ProbeApprove");
            logger.LogInformation("Probe: PATCH approval test for updateId={Id} status={Status} scope={Scope}",
                request.UpdateId, request.ApprovalStatus, resolvedScope);

            var outcome = await action1.SetCatalogApprovalAsync(request.UpdateId, request.ApprovalStatus, resolvedScope, ct);
            return Results.Ok(new { success = outcome == ApprovalOutcome.Success, outcome = outcome.ToString(), updateId = request.UpdateId, approvalStatus = request.ApprovalStatus, scope = resolvedScope });
        });

        // ── Bulk approve / decline — start background job, return job ID immediately ──
        // POST /api/action1/catalog/approve
        // Body: { UpdateIds: ["id1","id2"], ApprovalStatus: "Approved" }
        // Returns: { jobId: "abc123" } — poll GET /api/action1/catalog/approve/{jobId} for progress
        group.MapPost("approve", (
            CatalogApproveRequest request,
            ApprovalJobStore jobStore,
            IOptions<Action1Options> opts,
            ILoggerFactory loggerFactory,
            IServiceScopeFactory scopeFactory) =>
        {
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

            var resolvedScope = request.Scope ?? opts.Value.ApprovalScope;
            var ids = request.UpdateIds.ToList();
            var jobId = jobStore.CreateJob(ids.Count);
            var logger = loggerFactory.CreateLogger("Broker.Action1.Catalog");

            // Fire-and-forget with a dedicated DI scope so Action1Client (typed HTTP client,
            // scoped lifetime) is NOT disposed when the HTTP request scope ends at 202 return.
            _ = Task.Run(() => RunApprovalJobAsync(jobId, ids, request.ApprovalStatus, resolvedScope,
                scopeFactory, jobStore, logger));

            return Results.Accepted($"/api/action1/catalog/approve/{jobId}", new { jobId });
        });

        // ── Poll job progress ─────────────────────────────────────────────────
        // GET /api/action1/catalog/approve/{jobId}
        group.MapGet("approve/{jobId}", (string jobId, ApprovalJobStore jobStore) =>
        {
            var status = jobStore.GetStatus(jobId);
            return status is null
                ? Results.NotFound(new { Message = $"Job '{jobId}' not found or has expired." })
                : Results.Ok(status);
        });

        return app;
    }

    /// <summary>
    /// Background worker: sequential approval with 300ms inter-request delay.
    /// Items that exhaust their per-item 429 retries are collected and re-attempted
    /// in a second pass after a 60-second cool-down.
    ///
    /// Owns its own DI scope so Action1Client (typed HTTP client, scoped lifetime)
    /// is not disposed when the originating HTTP request scope ends.
    /// </summary>
    private static async Task RunApprovalJobAsync(
        string jobId,
        IReadOnlyList<string> ids,
        string approvalStatus,
        string scope,
        IServiceScopeFactory scopeFactory,
        ApprovalJobStore jobStore,
        ILogger logger)
    {
        // Create a dedicated scope that lives for the duration of the background job.
        await using var jobScope = scopeFactory.CreateAsyncScope();
        var action1 = jobScope.ServiceProvider.GetRequiredService<Action1Client>();

        var rateLimited = new List<string>();

        // ── First pass ────────────────────────────────────────────────────────
        foreach (var id in ids)
        {
            try
            {
                var outcome = await action1.SetCatalogApprovalAsync(id, approvalStatus, scope);
                if (outcome == ApprovalOutcome.Success)
                    jobStore.RecordSuccess(jobId);
                else if (outcome == ApprovalOutcome.RateLimitExhausted)
                    rateLimited.Add(id);         // retry in second pass
                else if (outcome == ApprovalOutcome.NotSupported)
                    jobStore.RecordSkipped(jobId, id); // API doesn't support this package type
                else
                    jobStore.RecordFailure(jobId, id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Action1: unhandled error approving {Id}", id);
                jobStore.RecordFailure(jobId, id);
            }

            await Task.Delay(300);
        }

        // ── Second pass (rate-limited items only) ─────────────────────────────
        if (rateLimited.Count > 0)
        {
            logger.LogInformation(
                "Action1: job {JobId} — {Count} item(s) were rate-limited; retrying after 60s cool-down.",
                jobId, rateLimited.Count);
            await Task.Delay(TimeSpan.FromSeconds(60));

            foreach (var id in rateLimited)
            {
                try
                {
                    var outcome = await action1.SetCatalogApprovalAsync(id, approvalStatus, scope);
                    if (outcome == ApprovalOutcome.Success)
                        jobStore.RecordSuccess(jobId);
                    else
                        jobStore.RecordFailure(jobId, id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Action1: unhandled error on retry for {Id}", id);
                    jobStore.RecordFailure(jobId, id);
                }

                await Task.Delay(500); // slightly more conservative on second pass
            }
        }

        jobStore.Complete(jobId);
        var final = jobStore.GetStatus(jobId);
        logger.LogInformation(
            "Action1: job {JobId} complete — {Succeeded} succeeded, {Failed} failed, {Skipped} skipped (manual approval required) of {Total}.",
            jobId, final?.Succeeded, final?.Failed, final?.Skipped, final?.Total);
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
public sealed record ProbeApproveRequest(string UpdateId, string ApprovalStatus = "Approved", string? Scope = null);

/// <summary>Bulk catalog approval/decline request.</summary>
public sealed record CatalogApproveRequest(
    IReadOnlyList<string> UpdateIds,
    string ApprovalStatus = "Approved",
    string? Scope = null);

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
