using HomeManagement.Abstractions.CrossCutting;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace HomeManagement.Core;

/// <summary>
/// Per-request middleware that establishes a correlation ID, propagates it into
/// <see cref="ICorrelationContext"/> (so domain services can read it), emits it on
/// the response as <c>X-Correlation-ID</c>, and pushes it into the Serilog
/// <see cref="LogContext"/> so every log entry emitted during the request carries
/// a <c>CorrelationId</c> property — enabling full request tracing in Seq.
/// </summary>
/// <remarks>
/// Register with <c>app.UseMiddleware&lt;CorrelationIdMiddleware&gt;()</c> before
/// <c>UseAuthentication</c> and <c>UseSerilogRequestLogging</c>.
/// Refit and SignalR clients should forward the header to preserve the chain
/// across service boundaries.
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>Standard header name carried on both requests and responses.</summary>
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlationContext)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N");

        // Echo the correlation ID back so callers can trace their request
        context.Response.Headers.TryAdd(HeaderName, correlationId);

        // Push into the ambient correlation context (available to domain services)
        using var _ = correlationContext.BeginScope(correlationId);

        // Push into Serilog LogContext so all log entries within this request carry CorrelationId
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}
