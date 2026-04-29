using HomeManagement.Abstractions.Models;
using Refit;

namespace HomeManagement.Web.Services;

/// <summary>
/// Refit-generated typed HTTP client for the Auth REST API.
/// </summary>
public interface IAuthApi
{
    [Post("/api/auth/login")]
    Task<AuthResult> LoginAsync([Body] LoginRequest request, CancellationToken ct = default);

    [Post("/api/auth/refresh")]
    Task<AuthResult> RefreshAsync([Body] WebRefreshRequest request, CancellationToken ct = default);

    [Post("/api/auth/revoke")]
    Task RevokeAsync([Body] WebRevokeRequest request, CancellationToken ct = default);
}

public sealed record WebRefreshRequest(string RefreshToken);

public sealed record WebRevokeRequest(string RefreshToken);
