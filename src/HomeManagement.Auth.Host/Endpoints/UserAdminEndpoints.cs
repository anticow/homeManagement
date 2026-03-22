using HomeManagement.Auth;

namespace HomeManagement.Auth.Host.Endpoints;

/// <summary>
/// Admin endpoints for user and role management.
/// </summary>
public static class UserAdminEndpoints
{
    public static void MapUserAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin"));

        group.MapGet("/users", GetUsers);
        group.MapPost("/users", CreateUser);
        group.MapPut("/users/{id:guid}/roles", AssignRoles);
        group.MapGet("/roles", GetRoles);
    }

    private static async Task<IResult> GetUsers(AuthService authService, CancellationToken ct)
    {
        return Results.Ok(await authService.GetUsersAsync(ct));
    }

    private static async Task<IResult> CreateUser(CreateUserRequest request, AuthService authService, CancellationToken ct)
    {
        var user = await authService.CreateLocalUserAsync(
            new CreateUserCommand(request.Username, request.Password, request.DisplayName, request.Email, request.Roles),
            ct);

        return Results.Created($"/api/admin/users/{user.UserId}", user);
    }

    private static async Task<IResult> AssignRoles(Guid id, AssignRolesRequest request, AuthService authService, CancellationToken ct)
    {
        return Results.Ok(await authService.AssignRolesAsync(id, request.Roles, ct));
    }

    private static async Task<IResult> GetRoles(AuthService authService, CancellationToken ct)
    {
        return Results.Ok(await authService.GetRolesAsync(ct));
    }
}

public sealed record CreateUserRequest(string Username, string Password, string DisplayName, string Email, List<string> Roles);
public sealed record AssignRolesRequest(List<string> Roles);
