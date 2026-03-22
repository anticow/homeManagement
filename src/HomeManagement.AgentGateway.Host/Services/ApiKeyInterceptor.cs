using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HomeManagement.AgentGateway.Host.Services;

/// <summary>
/// gRPC server interceptor that validates an API key from the "x-agent-api-key" metadata header.
/// Agents must present the pre-shared key to establish a connection.
/// </summary>
internal sealed class ApiKeyInterceptor : Interceptor
{
    private const string HeaderName = "x-agent-api-key";
    private readonly string _expectedKey;
    private readonly ILogger<ApiKeyInterceptor> _logger;

    public ApiKeyInterceptor(IConfiguration configuration, ILogger<ApiKeyInterceptor> logger)
    {
        _expectedKey = configuration["AgentGateway:ApiKey"]
            ?? throw new InvalidOperationException(
                "AgentGateway:ApiKey must be configured. Set via environment variable or appsettings.");

        if (_expectedKey.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AgentGateway:ApiKey still contains the placeholder value. " +
                "Generate a random key and set it via environment variable or secrets manager.");
        }

        _logger = logger;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ValidateApiKey(context);
        return continuation(request, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ValidateApiKey(context);
        return continuation(request, responseStream, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ValidateApiKey(context);
        return continuation(requestStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ValidateApiKey(context);
        return continuation(requestStream, responseStream, context);
    }

    private void ValidateApiKey(ServerCallContext context)
    {
        var apiKey = context.RequestHeaders.GetValue(HeaderName);

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("gRPC call from {Peer} rejected — missing {Header} header",
                context.Peer, HeaderName);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "API key required."));
        }

        if (!string.Equals(apiKey, _expectedKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("gRPC call from {Peer} rejected — invalid API key", context.Peer);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid API key."));
        }
    }
}
