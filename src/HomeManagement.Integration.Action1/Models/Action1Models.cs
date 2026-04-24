namespace HomeManagement.Integration.Action1.Models;

// ── Action1 API Response Models ────────────────────────────────────────────────
// These map to the Action1 REST API v1 response shapes.
// See: https://api.action1.com/docs

/// <summary>Represents a managed endpoint registered in Action1.</summary>
public sealed record Action1Endpoint(
    string Id,
    string Name,
    string IpAddress,
    string OsName,
    string OsVersion,
    string OsType,           // "Windows" | "Linux"
    string Status,           // "Online" | "Offline"
    DateTime LastSeenUtc,
    string? GroupId,
    string? GroupName);

/// <summary>Represents a patch available on an endpoint.</summary>
public sealed record Action1Patch(
    string Id,
    string Title,
    string Description,
    string Severity,         // "Critical" | "Important" | "Moderate" | "Low" | "None"
    string Category,         // "Security" | "NonSecurity" | "Driver" | "FeaturePack"
    long SizeBytes,
    bool RequiresReboot,
    DateTime PublishedUtc,
    string KbArticleId);

/// <summary>Represents a patch deployment operation.</summary>
public sealed record Action1Deployment(
    string Id,
    string EndpointId,
    string Status,           // "Pending" | "InProgress" | "Succeeded" | "Failed" | "Cancelled"
    DateTime CreatedUtc,
    DateTime? CompletedUtc,
    IReadOnlyList<Action1DeploymentResult> Results);

/// <summary>Per-patch result within a deployment.</summary>
public sealed record Action1DeploymentResult(
    string PatchId,
    string Title,
    string Status,           // "Installed" | "Failed" | "Skipped"
    string? ErrorMessage,
    bool RebootRequired);

/// <summary>A software item from Action1 software inventory.</summary>
public sealed record Action1SoftwareItem(
    string Name,
    string Version,
    string Publisher,
    DateTime? InstalledUtc);

/// <summary>Envelope for Action1 paginated list responses.</summary>
public sealed record Action1PagedResponse<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageSize,
    int PageNumber);

// ── Webhook Models ────────────────────────────────────────────────────────────

/// <summary>Root payload for Action1 webhook POST.</summary>
public sealed record Action1WebhookPayload(
    string EventType,
    string EventId,
    DateTime OccurredAtUtc,
    Action1WebhookEndpointRef? Endpoint,
    Action1WebhookDeploymentRef? Deployment);

public sealed record Action1WebhookEndpointRef(string Id, string Name);

public sealed record Action1WebhookDeploymentRef(
    string Id,
    string Status,
    int SucceededCount,
    int FailedCount);

// ── Known event type constants ─────────────────────────────────────────────────
public static class Action1EventTypes
{
    public const string PatchDeploymentCompleted = "patch_deployment_completed";
    public const string PatchDeploymentFailed    = "patch_deployment_failed";
    public const string EndpointConnected        = "endpoint_connected";
    public const string EndpointDisconnected     = "endpoint_disconnected";
}
