namespace HomeManagement.AgentGateway.Host.Services;

public sealed class AgentGatewayHostOptions
{
    public const string SectionName = "AgentGateway";
    public const int MinimumApiKeyBytes = 32;

    /// <summary>Shared API key used by internal REST control-plane clients.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Per-agent API keys keyed by agent ID.</summary>
    public Dictionary<string, string> AgentApiKeys { get; set; } = [];

    /// <summary>
    /// Optional JSON-encoded dictionary of agent API keys (e.g., from a single environment variable).
    /// Merged with <see cref="AgentApiKeys"/> at startup; duplicate agent IDs in this value override
    /// the structured section.
    /// </summary>
    public string? AgentApiKeysJson { get; set; }

    public static bool HasValidControlPlaneApiKey(AgentGatewayHostOptions options)
    {
        if (options.ApiKey.Contains("CHANGE-ME", StringComparison.OrdinalIgnoreCase)
            || options.ApiKey.Contains("CHANGEME", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Span<byte> decodedKey = stackalloc byte[options.ApiKey.Length];
        if (!Convert.TryFromBase64String(options.ApiKey, decodedKey, out var bytesWritten)
            || bytesWritten != MinimumApiKeyBytes)
        {
            return false;
        }
        if (!string.Equals(
                Convert.ToBase64String(decodedKey[..bytesWritten]),
                options.ApiKey,
                StringComparison.Ordinal))
        {
            return false;
        }

        var firstByte = decodedKey[0];
        foreach (var value in decodedKey[1..bytesWritten])
        {
            if (value != firstByte)
            {
                return true;
            }
        }

        return false;
    }
}
