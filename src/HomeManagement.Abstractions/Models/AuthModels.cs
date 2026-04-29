using System.Text.Json.Serialization;

namespace HomeManagement.Abstractions.Models;

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
/// RBAC role definition — used for role seeding and admin endpoints.
/// </summary>
public sealed record RoleDefinition(
    Guid RoleId,
    string Name,
    string Description,
    IReadOnlyList<string> Permissions);

/// <summary>
/// Local authentication user including password hash, for use during login only.
/// WARNING: Never return this type from an API endpoint. PasswordHash is excluded
/// from JSON serialization as a defence-in-depth measure.
/// </summary>
public sealed record AuthLocalUser(
    Guid UserId,
    string Username,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc,
    /// <summary>Excluded from serialization. For internal login verification only.</summary>
    [property: JsonIgnore] string PasswordHash);

/// <summary>
/// Minimal refresh token state for validation without loading the full token entity.
/// </summary>
public sealed record RefreshTokenState(
    Guid UserId,
    DateTime ExpiresUtc,
    DateTime? RevokedUtc);

/// <summary>
/// Role reference — ID and name for role assignment.
/// </summary>
public sealed record RoleRef(Guid Id, string Name);

/// <summary>
/// Data needed to create a new local user in the repository.
/// </summary>
public sealed record NewLocalUserRecord(
    Guid Id,
    string Username,
    string DisplayName,
    string Email,
    string PasswordHash,
    IReadOnlyList<Guid> RoleIds);
