using System.Text.Json.Serialization;

namespace HomeManagement.Integration.Action1.Models;

// ── Action1 API Response Models ────────────────────────────────────────────────
// These map to the Action1 REST API v3.0 response shapes.
// See: https://app.action1.com/apidocs/
//
// IMPORTANT: Action1 uses snake_case JSON property names.
// All records use [JsonPropertyName] attributes to ensure correct deserialization.
//
// Date fields: Action1 returns Unix timestamps (integer seconds) for most date fields.
// All DateTime / DateTime? properties use UnixOrIsoDateTimeConverter to handle
// both Unix seconds and ISO 8601 strings transparently.

/// <summary>Envelope for Action1 paginated list responses.</summary>
public sealed record Action1PagedResponse<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("total_items")]
    [property: JsonConverter(typeof(FlexibleIntConverter))]
    int TotalCount);

/// <summary>Represents a managed endpoint registered in Action1.</summary>
public sealed record Action1Endpoint(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("address")] string IpAddress,
    [property: JsonPropertyName("OS")] string OsName,
    [property: JsonPropertyName("platform")] string OsType,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("last_seen")]
    [property: JsonConverter(typeof(UnixOrIsoDateTimeConverter))]
    DateTime? LastSeenUtc,
    [property: JsonPropertyName("agent_version")] string? AgentVersion,
    [property: JsonPropertyName("missing_critical_updates")]
    [property: JsonConverter(typeof(FlexibleIntConverter))]
    int MissingCriticalUpdates,
    [property: JsonPropertyName("missing_other_updates")]
    [property: JsonConverter(typeof(FlexibleIntConverter))]
    int MissingOtherUpdates,
    [property: JsonPropertyName("user")] string? LastLoggedInUser,
    [property: JsonPropertyName("external_address")] string? ExternalAddress);

/// <summary>
/// Represents a missing (available to install) patch/update on an endpoint.
///
/// Data comes from GET /updates/{orgId}?endpoint_id={endpointId}
/// Action1 uses "name" for the package display name. The "version" field is required
/// when creating a policy instance deployment.
/// </summary>
public sealed record Action1Patch(
    [property: JsonPropertyName("id")] string Id,
    // Action1 /updates API uses "name" not "title"; "title" kept as fallback alias
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("size_bytes")]
    [property: JsonConverter(typeof(FlexibleIntConverter))]
    int SizeBytes,
    [property: JsonPropertyName("requires_reboot")] bool RequiresReboot,
    [property: JsonPropertyName("published_date")]
    [property: JsonConverter(typeof(UnixOrIsoDateTimeConverter))]
    DateTime? PublishedUtc,
    [property: JsonPropertyName("kb_article")] string? KbArticleId);

/// <summary>
/// Response from POST /policies/instances/{orgId} (one-time deployment/remediation).
/// Also used by GET /policies/instances/{orgId}/{id} for status polling.
/// </summary>
public sealed record Action1PolicyInstance(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("last_run")]
    [property: JsonConverter(typeof(UnixOrIsoDateTimeConverter))]
    DateTime? LastRunUtc,
    [property: JsonPropertyName("next_run")]
    [property: JsonConverter(typeof(UnixOrIsoDateTimeConverter))]
    DateTime? NextRunUtc);

/// <summary>
/// Status summary for a policy instance used to poll deployment completion.
/// Terminal statuses: Completed, Failed, Disabled.
/// </summary>
public sealed record Action1PolicyInstanceStatus(
    string Id,
    string? Status,
    IReadOnlyList<Action1PolicyEndpointResult> EndpointResults);

/// <summary>Per-endpoint result within a policy instance run.</summary>
public sealed record Action1PolicyEndpointResult(
    [property: JsonPropertyName("endpoint_id")] string EndpointId,
    [property: JsonPropertyName("endpoint_name")] string? EndpointName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error_message")] string? ErrorMessage,
    [property: JsonPropertyName("reboot_required")] bool RebootRequired);

/// <summary>A patch + version pair, required when creating a policy instance deployment.</summary>
public sealed record PatchToInstall(string Id, string? Version);

/// <summary>
/// Legacy deployment model — kept for backward compatibility with GetDeploymentAsync.
/// New code should use Action1PolicyInstance.
/// </summary>
public sealed record Action1Deployment(
    string Id,
    string? Status,
    IReadOnlyList<Action1DeploymentResult> Results);

/// <summary>Per-patch result within a deployment (legacy).</summary>
public sealed record Action1DeploymentResult(
    string PatchId,
    string Title,
    string Status,
    string? ErrorMessage,
    bool RebootRequired);

/// <summary>A software item from Action1 software inventory.</summary>
public sealed record Action1SoftwareItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("publisher")] string? Publisher,
    [property: JsonPropertyName("install_date")]
    [property: JsonConverter(typeof(UnixOrIsoDateTimeConverter))]
    DateTime? InstalledUtc);

/// <summary>OAuth2 token response from Action1.</summary>
internal sealed record Action1TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string TokenType);

// ── Installed updates ─────────────────────────────────────────────────────────

/// <summary>
/// Represents an update that has been installed on a managed endpoint.
/// Source: GET /updates/installed/{orgId}?fields=*
///
/// Used to determine the "last patched" date per endpoint for compliance tracking.
/// </summary>
public sealed record Action1InstalledUpdate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("endpoint_id")] string? EndpointId,
    [property: JsonPropertyName("endpoint_name")] string? EndpointName,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("install_date")]
    [property: JsonConverter(typeof(UnixOrIsoDateTimeConverter))]
    DateTime? InstallDate);

// ── Vulnerabilities (CVE correlation) ─────────────────────────────────────────

/// <summary>
/// A CVE with known available remediations in Action1.
/// Source: GET /Vulnerabilities/{orgId}?fields=*
///
/// Action1 correlates CVEs from NVD with packages in the software repository,
/// providing actionable remediation data per vulnerability.
/// </summary>
public sealed record Action1Vulnerability(
    [property: JsonPropertyName("cve_id")] string CveId,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("cvss_score")] double? CvssScore,
    [property: JsonPropertyName("published")]
    [property: JsonConverter(typeof(UnixOrIsoDateTimeConverter))]
    DateTime? PublishedUtc,
    [property: JsonPropertyName("software")] IReadOnlyList<Action1VulnerableSoftware>? Software);

/// <summary>Software affected by a CVE, with available update packages.</summary>
public sealed record Action1VulnerableSoftware(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("available_updates")] IReadOnlyList<Action1VulnerabilityUpdate>? AvailableUpdates);

/// <summary>An update package that remediates a CVE.</summary>
public sealed record Action1VulnerabilityUpdate(
    [property: JsonPropertyName("package_id")] string PackageId,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("name")] string? Name);
