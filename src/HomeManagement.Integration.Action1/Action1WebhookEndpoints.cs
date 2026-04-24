using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Integration.Action1.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// Minimal API endpoints for receiving Action1 webhook events.
/// Mount via <c>app.MapAction1WebhookEndpoints()</c> in Program.cs.
///
/// Security: validates HMAC-SHA256 signature on every request using
/// <see cref="Action1Options.WebhookSecret"/>. Requests with invalid or
/// missing signatures are rejected with 401.
/// </summary>
public static class Action1WebhookEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public static IEndpointRouteBuilder MapAction1WebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/action1/webhook", HandleWebhookAsync)
           .WithTags("Action1")
           .WithName("Action1Webhook")
           .AllowAnonymous(); // Auth is via HMAC signature validation below

        return app;
    }

    private static async Task<IResult> HandleWebhookAsync(
        HttpRequest request,
        IOptions<Action1Options> options,
        IPatchHistoryRepository patchHistory,
        IAuditLogger audit,
        ILogger<Action1Client> logger,
        CancellationToken ct)
    {
        // ── 1. Validate HMAC-SHA256 signature ─────────────────────────────────
        if (!request.Headers.TryGetValue("X-Action1-Signature", out var signatureHeader))
        {
            logger.LogWarning("Action1 webhook: missing X-Action1-Signature header");
            return Results.Unauthorized();
        }

        request.EnableBuffering();
        var body = await ReadBodyAsync(request, ct);
        request.Body.Position = 0;

        if (!ValidateSignature(body, signatureHeader.ToString(), options.Value.WebhookSecret))
        {
            logger.LogWarning("Action1 webhook: invalid HMAC-SHA256 signature — rejecting request");
            return Results.Unauthorized();
        }

        // ── 2. Deserialize payload ─────────────────────────────────────────────
        Action1WebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Action1WebhookPayload>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Action1 webhook: failed to deserialize payload");
            return Results.BadRequest("Invalid JSON payload");
        }

        if (payload is null)
            return Results.BadRequest("Empty payload");

        logger.LogInformation("Action1 webhook received: EventType={EventType} EventId={EventId}",
            payload.EventType, payload.EventId);

        // ── 3. Handle event types ──────────────────────────────────────────────
        switch (payload.EventType)
        {
            case Action1EventTypes.PatchDeploymentCompleted:
            case Action1EventTypes.PatchDeploymentFailed:
                await HandleDeploymentEventAsync(payload, patchHistory, audit, logger, ct);
                break;

            case Action1EventTypes.EndpointConnected:
            case Action1EventTypes.EndpointDisconnected:
                await HandleEndpointEventAsync(payload, audit, logger, ct);
                break;

            default:
                logger.LogDebug("Action1 webhook: unhandled event type {EventType}", payload.EventType);
                break;
        }

        return Results.Ok();
    }

    private static async Task HandleDeploymentEventAsync(
        Action1WebhookPayload payload,
        IPatchHistoryRepository patchHistory,
        IAuditLogger audit,
        ILogger logger,
        CancellationToken ct)
    {
        if (payload.Deployment is null) return;

        var succeeded = payload.Deployment.SucceededCount;
        var failed = payload.Deployment.FailedCount;
        var action = succeeded > 0 && failed == 0
            ? AuditAction.PatchInstallCompleted
            : AuditAction.PatchInstallFailed;
        var outcome = failed == 0 ? AuditOutcome.Success : (succeeded > 0 ? AuditOutcome.PartialSuccess : AuditOutcome.Failure);

        await audit.RecordAsync(new AuditEvent(
            EventId: Guid.NewGuid(),
            TimestampUtc: payload.OccurredAtUtc,
            CorrelationId: payload.EventId,
            Action: action,
            ActorIdentity: "action1-webhook",
            TargetMachineId: null,
            TargetMachineName: payload.Endpoint?.Name,
            Detail: $"Action1 deployment {payload.Deployment.Id}: {succeeded} succeeded, {failed} failed",
            Properties: null,
            Outcome: outcome,
            ErrorMessage: failed > 0 ? $"{failed} patches failed" : null), ct);

        logger.LogInformation(
            "Action1 webhook: deployment {Id} → {Succeeded} succeeded, {Failed} failed",
            payload.Deployment.Id, succeeded, failed);
    }

    private static async Task HandleEndpointEventAsync(
        Action1WebhookPayload payload,
        IAuditLogger audit,
        ILogger logger,
        CancellationToken ct)
    {
        var action = payload.EventType == Action1EventTypes.EndpointConnected
            ? AuditAction.AgentConnected
            : AuditAction.AgentDisconnected;

        await audit.RecordAsync(new AuditEvent(
            EventId: Guid.NewGuid(),
            TimestampUtc: payload.OccurredAtUtc,
            CorrelationId: payload.EventId,
            Action: action,
            ActorIdentity: "action1-webhook",
            TargetMachineId: null,
            TargetMachineName: payload.Endpoint?.Name,
            Detail: $"Action1 endpoint '{payload.Endpoint?.Name}' {payload.EventType}",
            Properties: null,
            Outcome: AuditOutcome.Success,
            ErrorMessage: null), ct);

        logger.LogInformation("Action1 webhook: endpoint {Name} {EventType}",
            payload.Endpoint?.Name, payload.EventType);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool ValidateSignature(byte[] body, string signatureHeader, string secret)
    {
        if (string.IsNullOrEmpty(secret)) return false;

        var key = Encoding.UTF8.GetBytes(secret);
        var computed = HMACSHA256.HashData(key, body);
        var computedHex = Convert.ToHexString(computed).ToLowerInvariant();

        // Action1 sends "sha256=<hex>"
        var provided = signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signatureHeader[7..]
            : signatureHeader;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHex),
            Encoding.UTF8.GetBytes(provided.ToLowerInvariant()));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
