using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HomeManagement.Data;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Auth;

/// <summary>
/// Application service for local authentication, refresh token lifecycle,
/// role assignment, and bootstrap seeding.
/// </summary>
public sealed class AuthService
{
    private readonly HomeManagementDbContext _db;
    private readonly JwtTokenService _jwtTokenService;
    private readonly AuthOptions _options;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        HomeManagementDbContext db,
        JwtTokenService jwtTokenService,
        IOptions<AuthOptions> options,
        ILogger<AuthService> logger)
    {
        _db = db;
        _jwtTokenService = jwtTokenService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        await _db.Database.MigrateAsync(ct);
        await SeedRolesAsync(ct);
        await SeedBootstrapAdminAsync(ct);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (request.Provider != AuthProviderType.Local)
        {
            return new AuthResult(false, Error: $"Provider '{request.Provider}' is not enabled in this release.");
        }

        var user = await _db.AuthUsers
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(u => u.Username == request.Username && u.Provider == AuthProviderType.Local.ToString(), ct);

        if (user is null || !LocalAuthProvider.VerifyPassword(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("Local authentication failed for user {Username}", request.Username);
            return new AuthResult(false, Error: "Invalid username or password.");
        }

        user.LastLoginUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return await IssueTokensAsync(user, ct);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = HashRefreshToken(refreshToken);
        var now = DateTime.UtcNow;
        var tokenState = await _db.AuthRefreshTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == tokenHash)
            .Select(t => new { t.UserId, t.ExpiresUtc, t.RevokedUtc })
            .SingleOrDefaultAsync(ct);

        if (tokenState is null || tokenState.RevokedUtc is not null || tokenState.ExpiresUtc <= now)
        {
            return new AuthResult(false, Error: "Refresh token is invalid or expired.");
        }

        var revokedUtc = DateTime.UtcNow;
        var rowsUpdated = await _db.AuthRefreshTokens
            .Where(t => t.TokenHash == tokenHash && t.RevokedUtc == null && t.ExpiresUtc > revokedUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.RevokedUtc, revokedUtc), ct);

        if (rowsUpdated != 1)
        {
            return new AuthResult(false, Error: "Refresh token is invalid or expired.");
        }

        var user = await _db.AuthUsers
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .SingleAsync(u => u.Id == tokenState.UserId, ct);

        return await IssueTokensAsync(user, ct);
    }

    public async Task<bool> RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = HashRefreshToken(refreshToken);
        var entry = await _db.AuthRefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        if (entry is null)
            return false;

        if (entry.RevokedUtc is null)
        {
            entry.RevokedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<IReadOnlyList<AuthUser>> GetUsersAsync(CancellationToken ct = default)
    {
        var users = await _db.AuthUsers
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);

        return users.Select(MapUser).ToList();
    }

    public async Task<AuthUser> CreateLocalUserAsync(CreateUserCommand command, CancellationToken ct = default)
    {
        var normalizedRoles = await ResolveRolesAsync(command.Roles, ct);

        if (await _db.AuthUsers.AnyAsync(u => u.Username == command.Username, ct))
            throw new InvalidOperationException($"User '{command.Username}' already exists.");

        if (await _db.AuthUsers.AnyAsync(u => u.Email == command.Email, ct))
            throw new InvalidOperationException($"Email '{command.Email}' is already in use.");

        var user = new AuthUserEntity
        {
            Id = Guid.NewGuid(),
            Username = command.Username,
            DisplayName = command.DisplayName,
            Email = command.Email,
            PasswordHash = LocalAuthProvider.HashPassword(command.Password),
            Provider = AuthProviderType.Local.ToString(),
            CreatedUtc = DateTime.UtcNow,
            UserRoles = normalizedRoles.Select(r => new AuthUserRoleEntity { UserId = Guid.Empty, RoleId = r.Id, Role = r }).ToList()
        };

        foreach (var userRole in user.UserRoles)
            userRole.UserId = user.Id;

        _db.AuthUsers.Add(user);
        await _db.SaveChangesAsync(ct);

        return await GetUserOrThrowAsync(user.Id, ct);
    }

    public async Task<AuthUser> AssignRolesAsync(Guid userId, IReadOnlyList<string> roles, CancellationToken ct = default)
    {
        var user = await _db.AuthUsers
            .Include(u => u.UserRoles)
            .SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException($"User '{userId}' not found.");

        var normalizedRoles = await ResolveRolesAsync(roles, ct);

        _db.AuthUserRoles.RemoveRange(user.UserRoles);
        user.UserRoles = normalizedRoles
            .Select(r => new AuthUserRoleEntity { UserId = user.Id, RoleId = r.Id })
            .ToList();

        await _db.SaveChangesAsync(ct);
        return await GetUserOrThrowAsync(userId, ct);
    }

    public async Task<IReadOnlyList<RoleDefinition>> GetRolesAsync(CancellationToken ct = default)
    {
        var roles = await _db.AuthRoles.OrderBy(r => r.Name).ToListAsync(ct);
        return roles.Select(MapRole).ToList();
    }

    public async Task<bool> UserHasRoleAsync(ClaimsPrincipal principal, string roleName, CancellationToken ct = default)
    {
        var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);

        if (!Guid.TryParse(userIdClaim, out var userId))
            return false;

        return await _db.AuthUserRoles
            .Include(ur => ur.Role)
            .AnyAsync(ur => ur.UserId == userId && ur.Role.Name == roleName, ct);
    }

    private async Task SeedRolesAsync(CancellationToken ct)
    {
        var existing = await _db.AuthRoles.Select(r => r.Name).ToListAsync(ct);
        var defaults = RbacService.GetDefaultRoles();

        foreach (var role in defaults.Where(role => !existing.Contains(role.Name, StringComparer.OrdinalIgnoreCase)))
        {
            _db.AuthRoles.Add(new AuthRoleEntity
            {
                Id = Guid.NewGuid(),
                Name = role.Name,
                Description = role.Description,
                PermissionsJson = System.Text.Json.JsonSerializer.Serialize(role.Permissions)
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task SeedBootstrapAdminAsync(CancellationToken ct)
    {
        var bootstrap = _options.BootstrapAdmin;
        if (!bootstrap.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(bootstrap.Username) || string.IsNullOrWhiteSpace(bootstrap.Password))
            throw new InvalidOperationException("Auth:BootstrapAdmin is enabled, but username or password is missing.");

        var existingAdmin = await _db.AuthUserRoles
            .Include(ur => ur.Role)
            .AnyAsync(ur => ur.Role.Name == "Admin", ct);

        if (existingAdmin)
            return;

        _logger.LogWarning("No admin users found. Seeding bootstrap admin account {Username}", bootstrap.Username);

        await CreateLocalUserAsync(new CreateUserCommand(
            bootstrap.Username,
            bootstrap.Password,
            bootstrap.DisplayName,
            bootstrap.Email,
            ["Admin"]), ct);
    }

    private async Task<AuthResult> IssueTokensAsync(AuthUserEntity userEntity, CancellationToken ct)
    {
        var user = MapUser(userEntity);
        var accessToken = _jwtTokenService.GenerateAccessToken(user);
        var refreshToken = JwtTokenService.GenerateRefreshToken();
        var issuedUtc = DateTime.UtcNow;

        _db.AuthRefreshTokens.Add(new AuthRefreshTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.UserId,
            TokenHash = HashRefreshToken(refreshToken),
            IssuedUtc = issuedUtc,
            ExpiresUtc = issuedUtc.Add(_options.RefreshTokenLifetime)
        });

        await _db.SaveChangesAsync(ct);

        return new AuthResult(
            true,
            user,
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresUtc: issuedUtc.Add(_options.AccessTokenLifetime));
    }

    private async Task<List<AuthRoleEntity>> ResolveRolesAsync(IReadOnlyList<string> roles, CancellationToken ct)
    {
        if (roles.Count == 0)
            throw new InvalidOperationException("At least one role must be assigned.");

        var normalized = roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existingRoles = await _db.AuthRoles.Where(r => normalized.Contains(r.Name)).ToListAsync(ct);

        var missing = normalized.Except(existingRoles.Select(r => r.Name), StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Unknown roles: {string.Join(", ", missing)}.");

        return existingRoles;
    }

    private async Task<AuthUser> GetUserOrThrowAsync(Guid userId, CancellationToken ct)
    {
        var user = await _db.AuthUsers
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .SingleAsync(u => u.Id == userId, ct);

        return MapUser(user);
    }

    private static AuthUser MapUser(AuthUserEntity entity)
    {
        var roles = entity.UserRoles
            .Select(ur => ur.Role.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AuthUser(
            entity.Id,
            entity.Username,
            entity.DisplayName,
            entity.Email,
            Enum.Parse<AuthProviderType>(entity.Provider, ignoreCase: true),
            roles,
            entity.CreatedUtc,
            entity.LastLoginUtc);
    }

    private static RoleDefinition MapRole(AuthRoleEntity entity)
    {
        var permissions = System.Text.Json.JsonSerializer.Deserialize<List<string>>(entity.PermissionsJson) ?? [];
        return new RoleDefinition(entity.Id, entity.Name, entity.Description, permissions);
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
