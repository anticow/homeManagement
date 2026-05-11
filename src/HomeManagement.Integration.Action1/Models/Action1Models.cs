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
    [property: JsonPropertyName("total_items")] int TotalCount);

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
    [property: JsonPropertyName("missing_critical_updates")] int MissingCriticalUpdates,
    [property: JsonPropertyName("missing_other_updates")] int MissingOtherUpdates,
    [property: JsonPropertyName("user")] string? LastLoggedInUser,
    [property: JsonPropertyName("external_address")] string? ExternalAddress);

/// <summary>
/// Represents a missing (available to install) patch on an endpoint.
/// NOTE: The exact path for per-endpoint patch listing is not publicly documented
/// in Action1's v3.0 API. This model maps the anticipated response shape.
/// </summary>
public sealed record Action1Patch(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("requires_reboot")] bool RequiresReboot,
    [property: JsonPropertyName("published_date")]
    [property: JsonConverter(typeof(UnixOrIsoDateTimeNonNullableConverter))]
    DateTime PublishedUtc,
    [property: JsonPropertyName("kb_article")] string? KbArticleId);

/// <summary>Result of a patch install operation.</summary>
public sealed record Action1Deployment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("endpoint_id")] string EndpointId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")]
    [property: JsonConverter(typeof(UnixOrIsoDateTimeNonNullableConverter))]
    DateTime CreatedUtc,
    [property: JsonPropertyName("completed_at")]
    [property: JsonConverter(typeof(UnixOrIsoDateTimeConverter))]
    DateTime? CompletedUtc,
    [property: JsonPropertyName("results")] IReadOnlyList<Action1DeploymentResult> Results);

/// <summary>Internal DTO returned when a deployment is first created.</summary>
internal sealed record Action1DeploymentCreated(
    [property: JsonPropertyName("id")] string Id);

/// <summary>Per-patch result within a deployment.</summary>
public sealed record Action1DeploymentResult(
    [property: JsonPropertyName("patch_id")] string PatchId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error_message")] string? ErrorMessage,
    [property: JsonPropertyName("reboot_required")] bool RebootRequired);

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
