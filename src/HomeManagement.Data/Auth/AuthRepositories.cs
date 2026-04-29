using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Auth;

public sealed class AuthUserRepository : IAuthUserRepository
{
    private readonly HomeManagementDbContext _db;

    public AuthUserRepository(HomeManagementDbContext db) => _db = db;

    public async Task<AuthLocalUser?> FindLocalUserForLoginAsync(string username, CancellationToken ct = default)
    {
        var entity = await _db.AuthUsers
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(u => u.Username == username && u.Provider == AuthProviderType.Local.ToString(), ct);

        if (entity is null)
            return null;

        var roles = entity.UserRoles
            .Select(ur => ur.Role.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AuthLocalUser(
            entity.Id,
            entity.Username,
            entity.DisplayName,
            entity.Email,
            roles,
            entity.CreatedUtc,
            entity.LastLoginUtc,
            entity.PasswordHash);
    }

    public async Task<AuthUser?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        var entity = await _db.AuthUsers
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(u => u.Id == userId, ct);

        return entity is null ? null : MapUser(entity);
    }

    public async Task<IReadOnlyList<AuthUser>> GetAllAsync(CancellationToken ct = default)
    {
        var users = await _db.AuthUsers
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);

        return users.Select(MapUser).ToList();
    }

    public Task<bool> ExistsByUsernameAsync(string username, CancellationToken ct = default) =>
        _db.AuthUsers.AnyAsync(u => u.Username == username, ct);

    public Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default) =>
        _db.AuthUsers.AnyAsync(u => u.Email == email, ct);

    public async Task CreateLocalUserAsync(NewLocalUserRecord user, CancellationToken ct = default)
    {
        var entity = new AuthUserEntity
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            PasswordHash = user.PasswordHash,
            Provider = AuthProviderType.Local.ToString(),
            CreatedUtc = DateTime.UtcNow,
            UserRoles = user.RoleIds.Select(roleId => new AuthUserRoleEntity { UserId = user.Id, RoleId = roleId }).ToList()
        };

        _db.AuthUsers.Add(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetLastLoginAsync(Guid userId, DateTime loginUtc, CancellationToken ct = default)
    {
        await _db.AuthUsers
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastLoginUtc, loginUtc), ct);
    }

    public async Task AssignRolesAsync(Guid userId, IReadOnlyList<Guid> roleIds, CancellationToken ct = default)
    {
        var user = await _db.AuthUsers
            .Include(u => u.UserRoles)
            .SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException($"User '{userId}' not found.");

        _db.AuthUserRoles.RemoveRange(user.UserRoles);
        user.UserRoles = roleIds
            .Select(roleId => new AuthUserRoleEntity { UserId = userId, RoleId = roleId })
            .ToList();

        await _db.SaveChangesAsync(ct);
    }

    public Task<bool> UserHasRoleAsync(Guid userId, string roleName, CancellationToken ct = default) =>
        _db.AuthUserRoles
            .Include(ur => ur.Role)
            .AnyAsync(ur => ur.UserId == userId && ur.Role.Name == roleName, ct);

    public Task<bool> HasAnyAdminAsync(CancellationToken ct = default) =>
        _db.AuthUserRoles
            .Include(ur => ur.Role)
            .AnyAsync(ur => ur.Role.Name == "Admin", ct);

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
}

public sealed class AuthRoleRepository : IAuthRoleRepository
{
    private readonly HomeManagementDbContext _db;

    public AuthRoleRepository(HomeManagementDbContext db) => _db = db;

    public async Task<IReadOnlyList<RoleDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        var roles = await _db.AuthRoles.OrderBy(r => r.Name).ToListAsync(ct);
        return roles.Select(MapRole).ToList();
    }

    public async Task<IReadOnlyList<string>> GetAllNamesAsync(CancellationToken ct = default) =>
        await _db.AuthRoles.Select(r => r.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<RoleRef>> GetByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default)
    {
        var normalized = names.ToList();
        var roles = await _db.AuthRoles
            .Where(r => normalized.Contains(r.Name))
            .ToListAsync(ct);
        return roles.Select(r => new RoleRef(r.Id, r.Name)).ToList();
    }

    public async Task SeedDefaultRolesAsync(IReadOnlyList<RoleDefinition> defaults, CancellationToken ct = default)
    {
        var existing = await _db.AuthRoles.Select(r => r.Name).ToListAsync(ct);

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

    private static RoleDefinition MapRole(AuthRoleEntity entity)
    {
        var permissions = System.Text.Json.JsonSerializer.Deserialize<List<string>>(entity.PermissionsJson) ?? [];
        return new RoleDefinition(entity.Id, entity.Name, entity.Description, permissions);
    }
}

public sealed class AuthRefreshTokenRepository : IAuthRefreshTokenRepository
{
    private readonly HomeManagementDbContext _db;

    public AuthRefreshTokenRepository(HomeManagementDbContext db) => _db = db;

    public async Task<RefreshTokenState?> GetTokenStateAsync(string tokenHash, CancellationToken ct = default)
    {
        return await _db.AuthRefreshTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == tokenHash)
            .Select(t => new RefreshTokenState(t.UserId, t.ExpiresUtc, t.RevokedUtc))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<bool> TryRevokeTokenAsync(string tokenHash, DateTime revokedUtc, CancellationToken ct = default)
    {
        var rowsUpdated = await _db.AuthRefreshTokens
            .Where(t => t.TokenHash == tokenHash && t.RevokedUtc == null && t.ExpiresUtc > revokedUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.RevokedUtc, revokedUtc), ct);

        return rowsUpdated == 1;
    }

    public async Task<bool> FindAndRevokeAsync(string tokenHash, CancellationToken ct = default)
    {
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

    public async Task AddAsync(Guid userId, string tokenHash, DateTime issuedUtc, DateTime expiresUtc, CancellationToken ct = default)
    {
        _db.AuthRefreshTokens.Add(new AuthRefreshTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = tokenHash,
            IssuedUtc = issuedUtc,
            ExpiresUtc = expiresUtc
        });

        await _db.SaveChangesAsync(ct);
    }
}

public sealed class AuthDatabaseInitializer : IAuthDatabaseInitializer
{
    private readonly HomeManagementDbContext _db;

    public AuthDatabaseInitializer(HomeManagementDbContext db) => _db = db;

    public Task MigrateAsync(CancellationToken ct = default) => _db.Database.MigrateAsync(ct);
}
