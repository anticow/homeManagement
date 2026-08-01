using System.Security.Cryptography;
using HomeManagement.AgentGateway.Host.Services;
using Microsoft.Extensions.Options;

namespace HomeManagement.AgentGateway.Host.Endpoints;

internal sealed class ControlPlaneApiKeyMiddleware
{
    internal const string HeaderName = "x-agent-gateway-api-key";

    private readonly RequestDelegate _next;
    private readonly byte[] _expectedKey;
    private readonly ILogger<ControlPlaneApiKeyMiddleware> _logger;

    public ControlPlaneApiKeyMiddleware(
        RequestDelegate next,
        IOptions<AgentGatewayHostOptions> options,
        ILogger<ControlPlaneApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _expectedKey = Convert.FromBase64String(options.Value.ApiKey);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/internal/agents"))
        {
            await _next(context);
            return;
        }

        var suppliedKey = context.Request.Headers[HeaderName].FirstOrDefault();
        if (!Matches(suppliedKey))
        {
            _logger.LogWarning(
                "Agent Gateway control-plane request from {RemoteIp} rejected due to a missing or invalid API key",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }

    private bool Matches(string? suppliedKey)
    {
        Span<byte> suppliedBytes = stackalloc byte[AgentGatewayHostOptions.MinimumApiKeyBytes];
        var hasValidEncoding = !string.IsNullOrEmpty(suppliedKey)
            && Convert.TryFromBase64String(suppliedKey, suppliedBytes, out var bytesWritten)
            && bytesWritten == AgentGatewayHostOptions.MinimumApiKeyBytes;
        var hasCanonicalEncoding = hasValidEncoding
            && string.Equals(
                Convert.ToBase64String(suppliedBytes),
                suppliedKey,
                StringComparison.Ordinal);

        var matches = CryptographicOperations.FixedTimeEquals(suppliedBytes, _expectedKey);
        if (!hasCanonicalEncoding)
        {
            return false;
        }

        return matches;
    }
}

public static class ControlPlaneApiKeyMiddlewareExtensions
{
    public static IApplicationBuilder UseControlPlaneApiKeyAuthentication(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ControlPlaneApiKeyMiddleware>();
    }
}
