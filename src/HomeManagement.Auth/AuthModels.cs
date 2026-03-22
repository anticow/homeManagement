namespace HomeManagement.Auth;

/// <summary>
/// Represents a system user with roles and claims.
/// </summary>
public sealed record AuthUser(
    Guid UserId,
    string Username,
    string DisplayName,
    string Email,
    AuthProviderType Provider,
    IReadOnlyList<string> Roles,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc);

/// <summary>
/// Authentication result returned by providers.
/// </summary>
public sealed record AuthResult(
    bool Success,
    AuthUser? User = null,
    string? Error = null,
    string? AccessToken = null,
    string? RefreshToken = null,
    DateTime? ExpiresUtc = null);

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
/// Login request from any provider.
/// </summary>
public sealed record LoginRequest(
    string Username,
    string Password,
    AuthProviderType Provider = AuthProviderType.Local);

/// <summary>
/// The type of authentication provider.
/// </summary>
public enum AuthProviderType
{
    Local,
    ActiveDirectory,
    Saml,
    OAuth
}

/// <summary>
/// RBAC role definition.
/// </summary>
public sealed record RoleDefinition(
    Guid RoleId,
    string Name,
    string Description,
    IReadOnlyList<string> Permissions);

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
