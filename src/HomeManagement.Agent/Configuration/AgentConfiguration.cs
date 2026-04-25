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
    public bool UseTls { get; set; } = true;
    public string CertPath { get; set; } = "certs/agent.pfx";
    public string? CertPassword { get; set; }
    public string CaCertPath { get; set; } = "certs/ca.crt";
    public string? ServerCaCertPath { get; set; }

    // ── Behavior ──
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int MaxConcurrentCommands { get; set; } = 5;
    public int CommandRateLimit { get; set; } = 10;
    public bool AllowElevation { get; set; }

    // ── Security ──
    public string[] DeniedCommandPatterns { get; set; } = [];

    // -- Logging ──
    public string LogLevel { get; set; } = "Information";
    public int LogRetentionDays { get; set; } = 7;
    /// <summary>
    /// Optional Seq server URL (e.g. <c>http://seq.seq.svc.cluster.local:5341</c>).
    /// When empty or whitespace, logs are written to console and file only.
    /// </summary>
    public string SeqUrl { get; set; } = string.Empty;

    // ── Update ──
    public bool AutoUpdateEnabled { get; set; } = true;
    public string UpdateStagingDir { get; set; } = "staging/";

    /// <summary>
    /// Raw 32-byte Ed25519 public key for verifying update package signatures.
    /// Loaded from the agent configuration file (base64-encoded) or provisioned
    /// alongside the mTLS certificate.
    /// </summary>
    public byte[]? UpdateSigningPublicKey { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException(
                "Agent:ApiKey must be configured before the agent starts.");
        }

        if (!UseTls && !IsLoopbackControlServer(ControlServer))
        {
            throw new InvalidOperationException(
                "Agent:UseTls may be false only when Agent:ControlServer targets localhost or another loopback address.");
        }
    }

    internal static bool IsLoopbackControlServer(string controlServer)
    {
        if (string.IsNullOrWhiteSpace(controlServer))
        {
            return false;
        }

        var candidate = controlServer.Contains("://", StringComparison.Ordinal)
            ? controlServer
            : $"http://{controlServer}";

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(controlServer, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(controlServer, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(controlServer, "[::1]", StringComparison.OrdinalIgnoreCase)
            || string.Equals(controlServer, "::1", StringComparison.OrdinalIgnoreCase);
    }
}
