using HomeManagement.Abstractions.Models;

namespace HomeManagement.Auth;

/// <summary>
/// Refresh token stored in the database.
/// </summary>
public sealed record RefreshTokenEntry(
    Guid Id,
    Guid UserId,
    string TokenHash,
    DateTime IssuedUtc,
    DateTime ExpiresUtc,
    bool IsRevoked);

/// <summary>
/// Standard RBAC permission constants.
/// </summary>
public static class Permissions
{
    public const string MachinesRead = "machines:read";
    public const string MachinesWrite = "machines:write";
    public const string PatchesRead = "patches:read";
    public const string PatchesApply = "patches:apply";
    public const string ServicesRead = "services:read";
    public const string ServicesControl = "services:control";
    public const string JobsRead = "jobs:read";
    public const string JobsSubmit = "jobs:submit";
    public const string JobsCancel = "jobs:cancel";
    public const string CredentialsRead = "credentials:read";
    public const string CredentialsWrite = "credentials:write";
    public const string AuditRead = "audit:read";
    public const string AuditExport = "audit:export";
    public const string AdminUsers = "admin:users";
    public const string AdminSettings = "admin:settings";
}
