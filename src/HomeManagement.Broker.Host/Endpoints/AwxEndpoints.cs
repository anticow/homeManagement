using HomeManagement.Broker.Host.Services;

namespace HomeManagement.Broker.Host.Endpoints;

public static class AwxEndpoints
{
    public static void MapAwxEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/awx")
            .WithTags("AWX")
            .RequireAuthorization();

        group.MapGet("/templates", async (IServiceProvider services, CancellationToken ct) =>
        {
            var awx = services.GetService<AwxClient>();
            if (awx is null) return Results.Ok(Array.Empty<AwxJobTemplate>());
            return Results.Ok((await awx.GetJobTemplatesAsync(ct)).Where(template => !AwxClient.IsWorkloadEnrollment(template)));
        });

        group.MapGet("/jobs", async (IServiceProvider services, int limit = 50, CancellationToken ct = default) =>
        {
            var awx = services.GetService<AwxClient>();
            if (awx is null) return Results.Ok(Array.Empty<AwxJob>());
            return Results.Ok(await awx.GetRecentJobsAsync(limit <= 0 ? 50 : limit, ct));
        });

        group.MapPost("/templates/{id:int}/launch", async (int id, IServiceProvider services, CancellationToken ct) =>
        {
            var awx = services.GetService<AwxClient>();
            if (awx is null) return Results.Problem("AWX integration is not enabled.");
            if (AwxClient.IsWorkloadEnrollment(await awx.GetJobTemplateAsync(id, ct)))
                return Results.Problem("Application packages and enrollment must be managed directly in AWX.", statusCode: 403);
            var jobId = await awx.LaunchJobTemplateAsync(id, ct);
            return jobId is null
                ? Results.Problem("AWX launch failed — check broker logs.")
                : Results.Ok(new { jobId });
        });
    }
}
