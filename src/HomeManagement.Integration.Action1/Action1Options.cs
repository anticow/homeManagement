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

    /// <summary>
    /// Configuration for HM-managed Action1 automation schedules.
    /// When enabled, the broker ensures these schedules exist in Action1 on startup.
    /// </summary>
    public ScheduleSyncOptions ScheduleSync { get; set; } = new();
}

/// <summary>
/// Options controlling which recurring patch deployment schedules homeManagement
/// creates and maintains in Action1.  Schedules are identified by name prefix
/// "homeManagement: " so HM can distinguish its own from manually created ones.
/// </summary>
public sealed class ScheduleSyncOptions
{
    /// <summary>When true, the schedule sync service runs on broker startup.</summary>
    public bool Enabled { get; set; }

    /// <summary>Rules — one rule produces one Action1 automation schedule.</summary>
    public List<ManagedScheduleRule> Rules { get; set; } = [];
}

/// <summary>
/// Describes one HM-managed Action1 automation schedule.
///
/// Example appsettings fragment:
/// <code>
/// "Action1": {
///   "ScheduleSync": {
///     "Enabled": true,
///     "Rules": [
///       {
///         "Name": "Critical patches (7-day soak)",
///         "Severities": ["Critical"],
///         "AutoApprove": true,
///         "DeferDays": 7,
///         "AllowReboot": false,
///         "ScheduleSettings": "RECURRING WEEKLY ON:3 AT:02-00-00 MAINTENANCE:1440"
///       }
///     ]
///   }
/// }
/// </code>
///
/// The <see cref="ScheduleSettings"/> field is passed verbatim to Action1 as the
/// automation's "settings" property, which controls execution timing.
///
/// Known formats (empirical; confirmed from PSAction1 module + live API):
///   One-time:   "ENABLED ONCE AT:HH-mm-ss DATE:yyyy-MM-dd"
///   Recurring:  "RECURRING WEEKLY ON:{dayNumber} AT:HH-mm-ss MAINTENANCE:{minutes}"
///               where dayNumber: 1=Mon, 2=Tue, 3=Wed, 4=Thu, 5=Fri, 6=Sat, 7=Sun
///   Disabled:   "DISABLED"  (schedule is created but will not run until enabled)
///
/// Tip: read an existing Action1 automation's "settings" field via the fleet schedules
/// API to confirm the exact format for your region/version.
/// </summary>
public sealed class ManagedScheduleRule
{
    /// <summary>Display name (without prefix). Will be stored in Action1 as "homeManagement: {Name}".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Severity values to filter — patches matching ANY listed severity are included.
    /// Valid Action1 values: "Critical", "Important", "Moderate", "Low", "Unspecified"
    /// Empty list means ALL severities.
    /// </summary>
    public List<string> Severities { get; set; } = [];

    /// <summary>
    /// When true, patches are automatically approved without waiting for manual review.
    /// When false, matching patches are queued for manual approval in the Action1 console.
    /// </summary>
    public bool AutoApprove { get; set; } = true;

    /// <summary>
    /// Days to wait after a patch's release date before auto-installing it (soak period).
    /// 0 = install immediately when detected. 7 = one week soak (recommended for Critical).
    /// Mapped to Action1 field: automatic_approval_delay_days.
    /// </summary>
    public int DeferDays { get; set; } = 7;

    /// <summary>Allow automatic reboot after patch installation.</summary>
    public bool AllowReboot { get; set; }

    /// <summary>
    /// Action1 schedule settings string controlling when the automation runs.
    /// Use "DISABLED" to create a schedule in a paused state for manual activation.
    /// See XML doc on <see cref="ManagedScheduleRule"/> for format details.
    /// </summary>
    public string ScheduleSettings { get; set; } = "DISABLED";

    /// <summary>
    /// Minutes Action1 will retry delivery to offline endpoints after the scheduled time.
    /// Default 1440 = 24 hours.
    /// </summary>
    public string RetryMinutes { get; set; } = "1440";

    /// <summary>Full Action1-stored name (with HM prefix).</summary>
    internal string FullName => $"homeManagement: {Name}";
}
