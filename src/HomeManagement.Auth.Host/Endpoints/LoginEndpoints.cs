using HomeManagement.Abstractions.Models;
using HomeManagement.Auth;

namespace HomeManagement.Auth.Host.Endpoints;

/// <summary>
/// Login endpoints — authenticate via local or Active Directory credentials.
/// </summary>
public static class LoginEndpoints
{
    public static void MapLoginEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", HandleLogin)
            .AllowAnonymous()
            .RequireRateLimiting("login");
    }

    private static async Task<IResult> HandleLogin(
        LoginRequest request,
        AuthService authService,
        CancellationToken ct)
    {
        var result = await authService.LoginAsync(request, ct);

        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }
}
