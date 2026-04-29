using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.AgentGateway.Host.Services;

public sealed class AgentApiKeyValidator : IAgentApiKeyValidator
{
    private const string HeaderName = "x-agent-api-key";
    private readonly IReadOnlyDictionary<string, string> _configuredKeys;
    private readonly ILogger<AgentApiKeyValidator> _logger;

    public AgentApiKeyValidator(IOptions<AgentGatewayHostOptions> options, ILogger<AgentApiKeyValidator> logger)
    {
        _configuredKeys = BuildKeys(options.Value);
        _logger = logger;

        if (_configuredKeys.Count == 0)
        {
            _logger.LogWarning("No per-agent API keys are configured for AgentGateway gRPC auth");
        }
    }

    public void ValidateOrThrow(ServerCallContext context, Handshake handshake)
    {
        if (string.IsNullOrWhiteSpace(handshake.AgentId))
        {
            _logger.LogWarning("gRPC call from {Peer} rejected — handshake missing agent id", context.Peer);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Agent ID required."));
        }

        var apiKey = context.RequestHeaders.GetValue(HeaderName);

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning(
                "gRPC call from {Peer} for {AgentId} rejected — missing {Header} header",
                context.Peer,
                handshake.AgentId,
                HeaderName);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "API key required."));
        }

        if (!_configuredKeys.TryGetValue(handshake.AgentId, out var expectedKey))
        {
            _logger.LogWarning(
                "gRPC call from {Peer} rejected — no API key configured for agent {AgentId}",
                context.Peer,
                handshake.AgentId);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Agent API key not configured."));
        }

        if (!Matches(apiKey, expectedKey))
        {
            _logger.LogWarning(
                "gRPC call from {Peer} rejected — invalid API key for agent {AgentId}",
                context.Peer,
                handshake.AgentId);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid API key."));
        }
    }

    internal static IReadOnlyDictionary<string, string> BuildKeys(AgentGatewayHostOptions options)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (agentId, apiKey) in options.AgentApiKeys)
        {
            if (!string.IsNullOrWhiteSpace(agentId) && !string.IsNullOrWhiteSpace(apiKey))
            {
                ValidateConfiguredKey(agentId, apiKey);
                keys[agentId] = apiKey;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.AgentApiKeysJson))
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(options.AgentApiKeysJson)
                ?? throw new InvalidOperationException("AgentGateway:AgentApiKeysJson must deserialize to an object.");

            foreach (var (agentId, apiKey) in parsed)
            {
                if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(apiKey))
                {
                    continue;
                }

                ValidateConfiguredKey(agentId, apiKey);
                keys[agentId] = apiKey;
            }
        }

        return keys;
    }

    private static void ValidateConfiguredKey(string agentId, string apiKey)
    {
        if (apiKey.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"AgentGateway agent API key for '{agentId}' still contains a placeholder value.");
        }
    }

    private static bool Matches(string suppliedKey, string expectedKey)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedKey);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);

        return suppliedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
