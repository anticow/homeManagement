namespace HomeManagement.Integration.Action1;

/// <summary>
/// Configuration for the Action1 RMM API integration.
/// Bind to "Action1" in appsettings. ApiKey must be injected from the
/// credential vault or an environment variable — never stored in appsettings.json.
/// </summary>
public sealed class Action1Options
{
    public const string Section = "Action1";

    /// <summary>Action1 REST API bearer token. Load from vault / env var at startup.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Action1 organization ID (visible in the Action1 console URL).</summary>
    public string OrganizationId { get; set; } = string.Empty;

    /// <summary>Action1 API base URL.</summary>
    public string BaseUrl { get; set; } = "https://api.action1.com/";

    /// <summary>
    /// HMAC-SHA256 secret used to validate inbound webhook payloads.
    /// Must match the secret configured in the Action1 webhook settings.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>How often the reconciliation sync job runs (minutes). Default 15.</summary>
    public int SyncIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// When true, the Action1 integration is active.
    /// When false, a no-op DisabledAction1PatchService is registered instead.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
