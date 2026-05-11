namespace HomeManagement.Integration.Action1;

/// <summary>
/// Configuration for the Action1 RMM API integration.
/// Bind to "Action1" in appsettings.
///
/// Credentials (ClientId / ClientSecret) must be injected from a Kubernetes secret
/// or environment variable — never stored in appsettings.json or Git.
///
/// Find your OrganizationId in the Action1 console URL:
///   https://app.action1.com/console/dashboard?org=YOUR-ORG-ID-HERE
///
/// Find your ClientId and ClientSecret in the Action1 console:
///   Settings → API Credentials → Create
/// </summary>
public sealed class Action1Options
{
    public const string Section = "Action1";

    /// <summary>Enable/disable the integration. When false, a no-op patch service is registered.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// OAuth2 Client ID from Action1 API Credentials page.
    /// Typically in format: api-key-xxxxx@action1.com
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth2 Client Secret from Action1 API Credentials page.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Action1 REST API base URL. Differs by region:
    ///   North America: https://app.action1.com/api/3.0/
    ///   Europe:        https://eu.action1.com/api/3.0/
    ///   Australia:     https://au.action1.com/api/3.0/
    /// </summary>
    public string BaseUrl { get; set; } = "https://app.action1.com/api/3.0/";

    /// <summary>
    /// Organization ID (GUID). Visible in the Action1 console URL:
    /// https://app.action1.com/console/dashboard?org=THIS-IS-YOUR-ORG-ID
    /// </summary>
    public string OrganizationId { get; set; } = string.Empty;

    /// <summary>How often the reconciliation sync job polls Action1 (minutes). Default 15.</summary>
    public int SyncIntervalMinutes { get; set; } = 15;
}
