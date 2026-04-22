using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Auditing;
using Microsoft.Extensions.Options;

namespace HomeManagement.Broker.Host.Endpoints;

/// <summary>
/// Audit log query, export, and chain-integrity verification endpoints.
/// </summary>
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/audit")
            .WithTags("Audit")
            .RequireAuthorization();

        group.MapGet("/", async (
            IAuditLogger audit,
            AuditAction? action,
            DateTime? fromUtc,
            DateTime? toUtc,
            int page,
            int pageSize,
            CancellationToken ct) =>
        {
            var query = new AuditQuery
            {
                Action = action,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                Page = page,
                PageSize = pageSize
            };
            return Results.Ok(await audit.QueryAsync(query, ct));
        });

        group.MapPost("/export", async (AuditQuery query, IAuditLogger audit, CancellationToken ct) =>
        {
            var stream = new MemoryStream();
            await audit.ExportAsync(query, stream, ExportFormat.Csv, ct);
            stream.Position = 0;
            return Results.File(stream, "text/csv", "audit-export.csv");
        });

        // Chain integrity verification — admin-only, O(n) full scan.
        // Returns { valid, verified, failedAtEventId } for the HMAC chain (v1 events only).
        group.MapPost("/verify-chain", async (
            IAuditEventRepository repository,
            IOptions<AuditOptions> options,
            CancellationToken ct) =>
        {
            var (valid, verified, failedAt) = await repository.VerifyChainAsync(options.Value.GetKeyBytes(), ct);
            return Results.Ok(new { valid, verified, failedAtEventId = failedAt });
        });
    }
}
