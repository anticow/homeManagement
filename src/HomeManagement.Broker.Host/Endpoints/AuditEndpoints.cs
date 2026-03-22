using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Broker.Host.Endpoints;

/// <summary>
/// Audit log query and export endpoints.
/// </summary>
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/audit")
            .WithTags("Audit")
            .RequireAuthorization();

        group.MapGet("/", async (IAuditLogger audit, int page, int pageSize, CancellationToken ct) =>
        {
            var query = new AuditQuery { Page = page, PageSize = pageSize };
            return Results.Ok(await audit.QueryAsync(query, ct));
        });

        group.MapPost("/export", async (AuditQuery query, IAuditLogger audit, CancellationToken ct) =>
        {
            var stream = new MemoryStream();
            await audit.ExportAsync(query, stream, ExportFormat.Csv, ct);
            stream.Position = 0;
            return Results.File(stream, "text/csv", "audit-export.csv");
        });
    }
}
