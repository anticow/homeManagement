using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Auth;

/// <summary>
/// Application service for local authentication, refresh token lifecycle,
/// role assignment, and bootstrap seeding.
/// </summary>
public sealed class AuthService
{
    private readonly IAuthUserRepository _users;
    private readonly IAuthRoleRepository _roles;
    private readonly IAuthRefreshTokenRepository _tokens;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly AuthOptions _options;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IAuthUserRepository users,
        IAuthRoleRepository roles,
        IAuthRefreshTokenRepository tokens,
        IJwtTokenService jwtTokenService,
        IPasswordPolicy passwordPolicy,
        IOptions<AuthOptions> options,
        ILogger<AuthService> logger)
    {
        _users = users;
        _roles = roles;
        _tokens = tokens;
        _jwtTokenService = jwtTokenService;
        _passwordPolicy = passwordPolicy;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Seed roles and bootstrap admin. Database migration must be done separately via IAuthDatabaseInitializer.
    /// </summary>
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        await SeedRolesAsync(ct);
        await SeedBootstrapAdminAsync(ct);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (request.Provider != AuthProviderType.Local)
        {
            return new AuthResult(false, Error: $"Provider '{request.Provider}' is not enabled in this release.");
        }

        var localUser = await _users.FindLocalUserForLoginAsync(request.Username, ct);

        if (localUser is null || !LocalAuthProvider.VerifyPassword(request.Password, localUser.PasswordHash))
        {
            _logger.LogWarning("Local authentication failed for user {Username}", request.Username);
            return new AuthResult(false, Error: "Invalid username or password.");
        }

        await _users.SetLastLoginAsync(localUser.UserId, DateTime.UtcNow, ct);

        var user = new AuthUser(
            localUser.UserId,
            localUser.Username,
            localUser.DisplayName,
            localUser.Email,
            AuthProviderType.Local,
            localUser.Roles,
            localUser.CreatedUtc,
            localUser.LastLoginUtc);

        return await IssueTokensAsync(user, ct);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = HashRefreshToken(refreshToken);
        var now = DateTime.UtcNow;
        var tokenState = await _tokens.GetTokenStateAsync(tokenHash, ct);

        if (tokenState is null || tokenState.RevokedUtc is not null || tokenState.ExpiresUtc <= now)
        {
            return new AuthResult(false, Error: "Refresh token is invalid or expired.");
        }

        var revokedUtc = DateTime.UtcNow;
        var revoked = await _tokens.TryRevokeTokenAsync(tokenHash, revokedUtc, ct);

        if (!revoked)
        {
            return new AuthResult(false, Error: "Refresh token is invalid or expired.");
        }

        var user = await _users.GetByIdAsync(tokenState.UserId, ct);
        if (user is null)
            return new AuthResult(false, Error: "Refresh token is invalid or expired.");

        return await IssueTokensAsync(user, ct);
    }

    public async Task<bool> RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = HashRefreshToken(refreshToken);
        return await _tokens.FindAndRevokeAsync(tokenHash, ct);
    }

    public Task<IReadOnlyList<AuthUser>> GetUsersAsync(CancellationToken ct = default) =>
        _users.GetAllAsync(ct);

    public async Task<AuthUser> CreateLocalUserAsync(CreateUserCommand command, CancellationToken ct = default)
    {
        _passwordPolicy.Validate(command.Password);

        var normalizedRoles = await ResolveRolesAsync(command.Roles, ct);

        if (await _users.ExistsByUsernameAsync(command.Username, ct))
            throw new InvalidOperationException($"User '{command.Username}' already exists.");

        if (await _users.ExistsByEmailAsync(command.Email, ct))
            throw new InvalidOperationException($"Email '{command.Email}' is already in use.");

        var newUser = new NewLocalUserRecord(
            Id: Guid.NewGuid(),
            Username: command.Username,
            DisplayName: command.DisplayName,
            Email: command.Email,
            PasswordHash: LocalAuthProvider.HashPassword(command.Password),
            RoleIds: normalizedRoles.Select(r => r.Id).ToList());

        await _users.CreateLocalUserAsync(newUser, ct);

        return await GetUserOrThrowAsync(newUser.Id, ct);
    }

    public async Task<AuthUser> AssignRolesAsync(Guid userId, IReadOnlyList<string> roles, CancellationToken ct = default)
    {
        var normalizedRoles = await ResolveRolesAsync(roles, ct);
        await _users.AssignRolesAsync(userId, normalizedRoles.Select(r => r.Id).ToList(), ct);
        return await GetUserOrThrowAsync(userId, ct);
    }

    public Task<IReadOnlyList<RoleDefinition>> GetRolesAsync(CancellationToken ct = default) =>
        _roles.GetAllAsync(ct);

    public async Task<bool> UserHasRoleAsync(ClaimsPrincipal principal, string roleName, CancellationToken ct = default)
    {
        var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);

        if (!Guid.TryParse(userIdClaim, out var userId))
            return false;

        return await _users.UserHasRoleAsync(userId, roleName, ct);
    }

    private async Task SeedRolesAsync(CancellationToken ct)
    {
        await _roles.SeedDefaultRolesAsync(RbacService.GetDefaultRoles(), ct);
    }

    private async Task SeedBootstrapAdminAsync(CancellationToken ct)
    {
        var bootstrap = _options.BootstrapAdmin;
        if (!bootstrap.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(bootstrap.Username) || string.IsNullOrWhiteSpace(bootstrap.Password))
            throw new InvalidOperationException("Auth:BootstrapAdmin is enabled, but username or password is missing.");

        _passwordPolicy.Validate(bootstrap.Password);

        if (await _users.HasAnyAdminAsync(ct))
        {
            _logger.LogWarning(
                "Auth:BootstrapAdmin is still enabled but an admin account already exists. " +
                "Disable BootstrapAdmin in configuration to remove the seeding password from memory.");
            return;
        }

        _logger.LogWarning("No admin users found. Seeding bootstrap admin account {Username}", bootstrap.Username);

        await CreateLocalUserAsync(new CreateUserCommand(
            bootstrap.Username,
            bootstrap.Password,
            bootstrap.DisplayName,
            bootstrap.Email,
            ["Admin"]), ct);
    }

    private async Task<AuthResult> IssueTokensAsync(AuthUser user, CancellationToken ct)
    {
        var accessToken = _jwtTokenService.GenerateAccessToken(user);
        var refreshToken = _jwtTokenService.GenerateRefreshToken();
        var issuedUtc = DateTime.UtcNow;

        await _tokens.AddAsync(
            user.UserId,
            HashRefreshToken(refreshToken),
            issuedUtc,
            issuedUtc.Add(_options.RefreshTokenLifetime),
            ct);

        return new AuthResult(
            true,
            user,
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresUtc: issuedUtc.Add(_options.AccessTokenLifetime));
    }

    private async Task<IReadOnlyList<RoleRef>> ResolveRolesAsync(IReadOnlyList<string> roles, CancellationToken ct)
    {
        if (roles.Count == 0)
            throw new InvalidOperationException("At least one role must be assigned.");

        var normalized = roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingRoles = await _roles.GetByNamesAsync(normalized, ct);

        var missing = normalized.Except(existingRoles.Select(r => r.Name), StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Unknown roles: {string.Join(", ", missing)}.");

        return existingRoles;
    }

    private async Task<AuthUser> GetUserOrThrowAsync(Guid userId, CancellationToken ct)
    {
        return await _users.GetByIdAsync(userId, ct)
            ?? throw new InvalidOperationException($"User '{userId}' not found.");
    }

    private static string HashRefreshToken(string refreshToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
        return Convert.ToBase64String(hash);
    }

}

public sealed record CreateUserCommand(
    string Username,
    string Password,
    string DisplayName,
    string Email,
    IReadOnlyList<string> Roles);
