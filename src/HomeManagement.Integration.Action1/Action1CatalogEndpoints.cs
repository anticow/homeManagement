using HomeManagement.Integration.Action1.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
            string approvalStatus = "New",
            CancellationToken ct = default) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            try
            {
                var updates = await action1.GetCatalogUpdatesAsync(approvalStatus, ct);
                return Results.Ok(updates.Select(MapCatalogDto).ToList());
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
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

        // ── Bulk approve / decline catalog updates ────────────────────────────
        // POST /api/action1/catalog/approve
        // Body: { UpdateIds: ["id1","id2"], ApprovalStatus: "Approved" }
        group.MapPost("approve", async (
            CatalogApproveRequest request,
            Action1Client action1,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
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

            try
            {
                // Fan out with limited concurrency — stay under Action1 rate limit.
                using var semaphore = new SemaphoreSlim(5);
                var tasks = request.UpdateIds.Select(async id =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        var ok = await action1.SetCatalogApprovalAsync(id, request.ApprovalStatus, ct);
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
                return Results.Problem(ex.Message, statusCode: 502, title: "Action1 API Error");
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

/// <summary>Bulk catalog approval/decline request.</summary>
public sealed record CatalogApproveRequest(
    IReadOnlyList<string> UpdateIds,
    string ApprovalStatus = "Approved");

/// <summary>Broker-side DTO for a catalog update item.</summary>
public sealed record CatalogUpdateDto(
    string Id,
    string Name,
    string? Version,
    string? Description,
    string Severity,
    string Category,
    string? UpdateType,
    string ApprovalStatus,
    bool RequiresReboot,
    DateTime? PublishedUtc,
    string? KbArticleId);
