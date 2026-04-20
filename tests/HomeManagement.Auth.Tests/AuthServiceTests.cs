using FluentAssertions;
using HomeManagement.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO;

namespace HomeManagement.Auth.Tests;

public sealed class AuthServiceTests : IDisposable
{
    private const string TestSigningKey = "this-is-a-test-signing-key-that-is-long-enough-for-hmac-sha256!";
    private readonly SqliteConnection _connection;
    private readonly HomeManagementDbContext _db;

    public AuthServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new HomeManagementDbContext(options);
    }

    [Fact]
    public async Task EnsureInitializedAsync_SeedsRolesAndBootstrapAdmin()
    {
        var service = CreateService(new AuthOptions
        {
            JwtSigningKey = TestSigningKey,
            Issuer = "test",
            Audience = "test",
            BootstrapAdmin = new BootstrapAdminOptions
            {
                Enabled = true,
                Username = "admin",
                Password = "AdminPassword123!",
                DisplayName = "Admin",
                Email = "admin@test.local"
            }
        });

        await service.EnsureInitializedAsync();

        var roles = await service.GetRolesAsync();
        roles.Should().Contain(r => r.Name == "Admin");

        var users = await service.GetUsersAsync();
        users.Should().ContainSingle(u => u.Username == "admin");
        users.Single().Roles.Should().Contain("Admin");
    }

    [Fact]
    public async Task LoginAsync_ValidLocalCredentials_IssuesTokens()
    {
        var service = CreateInitializedService();

        var created = await service.CreateLocalUserAsync(new CreateUserCommand(
            "operator",
            "OperatorPassword123!",
            "Operator User",
            "operator@test.local",
            ["Operator"]));

        var result = await service.LoginAsync(new LoginRequest("operator", "OperatorPassword123!"));

        result.Success.Should().BeTrue();
        result.User!.UserId.Should().Be(created.UserId);
        result.AccessToken.Should().NotBeNullOrEmpty();
        result.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RefreshAsync_ValidToken_RotatesRefreshToken()
    {
        var service = CreateInitializedService();
        await service.CreateLocalUserAsync(new CreateUserCommand(
            "operator",
            "OperatorPassword123!",
            "Operator User",
            "operator@test.local",
            ["Operator"]));

        var login = await service.LoginAsync(new LoginRequest("operator", "OperatorPassword123!"));
        var refresh = await service.RefreshAsync(login.RefreshToken!);

        refresh.Success.Should().BeTrue();
        refresh.RefreshToken.Should().NotBe(login.RefreshToken);

        var stale = await service.RefreshAsync(login.RefreshToken!);
        stale.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentRequests_OnlyOneSucceeds()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"hm-auth-refresh-{Guid.NewGuid():N}.db");

        try
        {
            await using (var seedContext = CreateFileDbContext(dbPath))
            {
                var seedService = CreateService(seedContext, new AuthOptions
                {
                    JwtSigningKey = TestSigningKey,
                    Issuer = "test",
                    Audience = "test"
                });

                await seedService.EnsureInitializedAsync();
                await seedService.CreateLocalUserAsync(new CreateUserCommand(
                    "operator",
                    "OperatorPassword123!",
                    "Operator User",
                    "operator@test.local",
                    ["Operator"]));

                var login = await seedService.LoginAsync(new LoginRequest("operator", "OperatorPassword123!"));

                await using var context1 = CreateFileDbContext(dbPath);
                await using var context2 = CreateFileDbContext(dbPath);

                var service1 = CreateService(context1, new AuthOptions
                {
                    JwtSigningKey = TestSigningKey,
                    Issuer = "test",
                    Audience = "test"
                });
                var service2 = CreateService(context2, new AuthOptions
                {
                    JwtSigningKey = TestSigningKey,
                    Issuer = "test",
                    Audience = "test"
                });

                var results = await Task.WhenAll(
                    service1.RefreshAsync(login.RefreshToken!),
                    service2.RefreshAsync(login.RefreshToken!));

                results.Count(result => result.Success).Should().Be(1);
                results.Count(result => !result.Success).Should().Be(1);
            }
        }
        finally
        {
            // EF Core SQLite connection pooling holds file handles on Windows after DisposeAsync.
            // Clear all pools to release the lock before deleting the temp file.
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task AssignRolesAsync_UpdatesPersistedRoles()
    {
        var service = CreateInitializedService();
        var user = await service.CreateLocalUserAsync(new CreateUserCommand(
            "viewer",
            "ViewerPassword123!",
            "Viewer User",
            "viewer@test.local",
            ["Viewer"]));

        var updated = await service.AssignRolesAsync(user.UserId, ["Admin", "Auditor"]);

        updated.Roles.Should().BeEquivalentTo(["Admin", "Auditor"]);
    }

    [Fact]
    public async Task CreateLocalUserAsync_InvalidRole_Throws()
    {
        var service = CreateInitializedService();

        var act = () => service.CreateLocalUserAsync(new CreateUserCommand(
            "user1",
            "Password123!",
            "User One",
            "user1@test.local",
            ["Ghost"]));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private AuthService CreateInitializedService()
    {
        var service = CreateService(_db, new AuthOptions
        {
            JwtSigningKey = TestSigningKey,
            Issuer = "test",
            Audience = "test"
        });

        service.EnsureInitializedAsync().GetAwaiter().GetResult();
        return service;
    }

    private static HomeManagementDbContext CreateFileDbContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        return new HomeManagementDbContext(options);
    }

    private AuthService CreateService(AuthOptions options)
    {
        return CreateService(_db, options);
    }

    private static AuthService CreateService(HomeManagementDbContext db, AuthOptions options)
    {
        var jwt = new JwtTokenService(Options.Create(options), NullLogger<JwtTokenService>.Instance);
        return new AuthService(db, jwt, Options.Create(options), NullLogger<AuthService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
