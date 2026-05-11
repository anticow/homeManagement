using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// Stub for Action1 webhook handling.
///
/// IMPORTANT: As of Action1 REST API v3.0, Action1 does NOT support outbound
/// webhook push to external URLs. This endpoint is kept as a stub in case
/// Action1 adds webhook support in a future API version.
///
/// For real-time event awareness, the Action1SyncJob polls Action1 on a
/// configurable schedule (default: every 15 minutes).
///
/// If Action1 adds webhook support, re-implement this class using HMAC-SHA256
/// signature validation on the X-Action1-Signature header.
/// </summary>
public static class Action1WebhookEndpoints
{
    public static IEndpointRouteBuilder MapAction1WebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // Intentional no-op — Action1 does not send outbound webhook events.
        // The route is registered to avoid 404s if someone misconfigures Action1
        // to POST to this URL in a future version.
        app.MapPost("/api/action1/webhook", (
            IOptions<Action1Options> opts,
            ILogger<Action1Client> logger) =>
        {
            logger.LogWarning(
                "Action1 webhook received, but Action1 does not currently support outbound webhooks. " +
                "This request will be ignored. Review Action1 API documentation for updates.");
            return Results.Ok(new { message = "Webhook received but not processed. Action1 webhook support pending." });
        })
        .WithTags("Action1")
        .WithName("Action1Webhook")
        .AllowAnonymous();

        return app;
    }
}
