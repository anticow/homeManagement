namespace HomeManagement.Agent.Configuration;

/// <summary>
/// Typed configuration bound from hm-agent.json.
/// </summary>
public sealed class AgentConfiguration
{
    public const string SectionName = "Agent";

    // ── Connection ──
    public string ControlServer { get; set; } = "localhost:9444";
    public string AgentId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;

    // ── Certificates ──
    public string CertPath { get; set; } = "certs/agent.pfx";
    public string? CertPassword { get; set; }
    public string CaCertPath { get; set; } = "certs/ca.crt";

    // ── Behavior ──
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int MaxConcurrentCommands { get; set; } = 5;
    public int CommandRateLimit { get; set; } = 10;
    public bool AllowElevation { get; set; }

    // ── Security ──
    public string[] DeniedCommandPatterns { get; set; } = [];

    // ── Logging ──
    public string LogLevel { get; set; } = "Information";
    public int LogRetentionDays { get; set; } = 7;

    // ── Update ──
    public bool AutoUpdateEnabled { get; set; } = true;
    public string UpdateStagingDir { get; set; } = "staging/";

    /// <summary>
    /// Raw 32-byte Ed25519 public key for verifying update package signatures.
    /// Loaded from the agent configuration file (base64-encoded) or provisioned
    /// alongside the mTLS certificate.
    /// </summary>
    public byte[]? UpdateSigningPublicKey { get; set; }
}
