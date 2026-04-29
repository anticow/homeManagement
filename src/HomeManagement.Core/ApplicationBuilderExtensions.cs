using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HomeManagement.Core;

public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Appends the standard HomeManagement security response headers on every request.
    /// </summary>
    public static IApplicationBuilder UseHomeManagementSecurityHeaders(
        this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            ctx.Response.Headers.Append("X-Frame-Options", "DENY");
            ctx.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
            ctx.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
            await next();
        });
    }

    /// <summary>
    /// Maps the standard health, readiness, version, and Prometheus scraping endpoints.
    /// </summary>
    public static WebApplication UseHomeManagementHealthEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/healthz");
        app.MapGet("/readyz", () => Results.Ok("ready"));
        app.MapGet("/version",
                () => new { version = Environment.GetEnvironmentVariable("APP_VERSION") ?? "unknown" })
            .AllowAnonymous().ExcludeFromDescription();
        app.MapPrometheusScrapingEndpoint();
        return app;
    }
}
