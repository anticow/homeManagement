using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Broker.Host.Endpoints;

/// <summary>
/// Job management endpoints.
/// </summary>
public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs")
            .WithTags("Jobs")
            .RequireAuthorization();

        group.MapGet("/", async (IJobScheduler scheduler, int page, int pageSize, CancellationToken ct) =>
        {
            var query = new JobQuery { Page = page, PageSize = pageSize };
            return Results.Ok(await scheduler.ListJobsAsync(query, ct));
        });

        group.MapGet("/{id:guid}", async (Guid id, IJobScheduler scheduler, CancellationToken ct) =>
        {
            var jobId = new JobId(id);
            var status = await scheduler.GetStatusAsync(jobId, ct);
            return status is not null ? Results.Ok(status) : Results.NotFound();
        });

        group.MapPost("/", async (JobDefinition job, IJobScheduler scheduler, CancellationToken ct) =>
        {
            var jobId = await scheduler.SubmitAsync(job, ct);
            return Results.Created($"/api/jobs/{jobId.Value}", new { jobId = jobId.Value });
        });

        group.MapDelete("/{id:guid}", async (Guid id, IJobScheduler scheduler, CancellationToken ct) =>
        {
            await scheduler.CancelAsync(new JobId(id), ct);
            return Results.NoContent();
        });
    }
}
