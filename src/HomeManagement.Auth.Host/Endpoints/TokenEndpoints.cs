using HomeManagement.Auth;

namespace HomeManagement.Auth.Host.Endpoints;

/// <summary>
/// Token management endpoints — refresh, revoke.
/// </summary>
public static class TokenEndpoints
{
    public static void MapTokenEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/refresh", HandleRefresh)
            .AllowAnonymous()
            .RequireRateLimiting("login");
        app.MapPost("/api/auth/revoke", HandleRevoke)
            .AllowAnonymous()
            .RequireRateLimiting("login");
    }

    private static async Task<IResult> HandleRefresh(
        RefreshRequest request,
        AuthService authService,
        CancellationToken ct)
    {
        var result = await authService.RefreshAsync(request.RefreshToken, ct);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }

    private static async Task<IResult> HandleRevoke(
        RevokeRequest request,
        AuthService authService,
        CancellationToken ct)
    {
        var revoked = await authService.RevokeAsync(request.RefreshToken, ct);
        return Results.Ok(new { revoked });
    }
}

public sealed record RefreshRequest(string RefreshToken);
public sealed record RevokeRequest(string RefreshToken);
