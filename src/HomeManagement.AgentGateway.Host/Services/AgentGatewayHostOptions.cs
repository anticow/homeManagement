namespace HomeManagement.AgentGateway.Host.Services;

public sealed class AgentGatewayHostOptions
{
    public const string SectionName = "AgentGateway";

    /// <summary>Per-agent API keys keyed by agent ID.</summary>
    public Dictionary<string, string> AgentApiKeys { get; set; } = [];

    /// <summary>
    /// Optional JSON-encoded dictionary of agent API keys (e.g., from a single environment variable).
    /// Merged with <see cref="AgentApiKeys"/> at startup; duplicate agent IDs in this value override
    /// the structured section.
    /// </summary>
    public string? AgentApiKeysJson { get; set; }
}
