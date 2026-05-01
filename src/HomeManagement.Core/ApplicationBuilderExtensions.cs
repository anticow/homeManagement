using HomeManagement.Abstractions.CrossCutting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// Adds a global exception handler that:
    /// <list type="bullet">
    ///   <item>Logs unhandled exceptions at Error level with the correlation ID so they are
    ///   traceable in Seq by searching <c>CorrelationId = "..."</c>.</item>
    ///   <item>Returns an RFC 7807 ProblemDetails JSON body with the <c>correlationId</c>
    ///   field populated, giving callers a key to look up the full error in Seq.</item>
    ///   <item>Silently skips writing a body when the response has already started
    ///   (streaming, file download, WebSocket upgrade).</item>
    ///   <item>Treats client-abort <see cref="OperationCanceledException"/> as debug-level
    ///   to avoid noisy error logs for normal browser navigation.</item>
    /// </list>
    /// Place this after <see cref="CorrelationIdMiddleware"/> so the Serilog LogContext
    /// already carries the <c>CorrelationId</c> property when the exception is logged.
    /// </summary>
    public static WebApplication UseHomeManagementExceptionHandler(this WebApplication app)
    {
        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var feature = context.Features.Get<IExceptionHandlerFeature>();
                if (feature?.Error is not { } exception) return;

                var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("HomeManagement.ExceptionHandler");

                // Client disconnected — noisy at Error level; log at Debug and bail.
                if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
                {
                    var m = context.Request.Method;
                    var p = context.Request.Path.ToString();
                    ExceptionHandlerLog.ClientAborted(logger, m, p);
                    return;
                }

                // Prefer the ambient CorrelationContext (set by CorrelationIdMiddleware).
                var correlationContext = context.RequestServices.GetService<ICorrelationContext>();
                var correlationId = correlationContext?.CorrelationId
                    ?? context.Request.Headers[CorrelationIdMiddleware.HeaderName].FirstOrDefault()
                    ?? "unknown";

                var method = context.Request.Method;
                var path = context.Request.Path.ToString();
                ExceptionHandlerLog.UnhandledException(logger, exception,
                    exception.GetType().Name, method, path, correlationId);

                // Cannot rewrite a response that has already started (streaming / file / WebSocket).
                if (context.Response.HasStarted) return;

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/problem+json";
                // Ensure the correlation ID header is present even if CorrelationIdMiddleware
                // already tried to set it before the exception occurred.
                context.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;

                await context.Response.WriteAsJsonAsync(new
                {
                    type = "https://tools.ietf.org/html/rfc7807",
                    title = "An unexpected error occurred.",
                    status = StatusCodes.Status500InternalServerError,
                    correlationId
                });
            });
        });

        return app;
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

/// <summary>High-performance log definitions for the global exception handler.</summary>
internal static partial class ExceptionHandlerLog
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Request aborted by client on {Method} {Path}")]
    internal static partial void ClientAborted(ILogger logger, string method, string path);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Unhandled {ExceptionType} on {Method} {Path} — search Seq: CorrelationId = \"{CorrelationId}\"")]
    internal static partial void UnhandledException(ILogger logger, Exception exception,
        string exceptionType, string method, string path, string correlationId);
}
