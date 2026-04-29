using FluentAssertions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Data;
using HomeManagement.Data.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomeManagement.Auth.Tests;

/// <summary>
/// Contract tests that verify AuthService correctly delegates to its injected
/// IJwtTokenService and IPasswordPolicy abstractions — using NSubstitute mocks.
/// </summary>
public sealed class AuthServiceContractTests : IDisposable
{
    private const string TestSigningKey = "contract-test-signing-key-long-enough-for-hmac256!";
    private readonly SqliteConnection _connection;
    private readonly HomeManagementDbContext _db;

    public AuthServiceContractTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new HomeManagementDbContext(dbOptions);
        _db.Database.EnsureCreated();
    }

    // ─── IPasswordPolicy delegation ───────────────────────────────────────────

    [Fact]
    public async Task CreateLocalUser_WhenPolicyThrows_PropagatesException()
    {
        var policy = Substitute.For<IPasswordPolicy>();
        policy.When(p => p.Validate(Arg.Any<string>()))
              .Do(_ => throw new InvalidOperationException("policy-rejection-message"));

        var service = CreateService(policy: policy);
        await service.EnsureInitializedAsync();

        var act = () => service.CreateLocalUserAsync(new CreateUserCommand(
            "u1", "anypassword", "User One", "u1@test.local", ["Viewer"]));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*policy-rejection-message*");
    }

    [Fact]
    public async Task CreateLocalUser_DelegatesToPasswordPolicy_WithProvidedPassword()
    {
        var policy = Substitute.For<IPasswordPolicy>();
        var service = CreateService(policy: policy);
        await service.EnsureInitializedAsync();

        const string password = "ValidPassword123!";
        await service.CreateLocalUserAsync(new CreateUserCommand(
            "u2", password, "User Two", "u2@test.local", ["Viewer"]));

        policy.Received(1).Validate(password);
    }

    [Fact]
    public async Task Login_PolicyIsNotInvokedAtLoginTime()
    {
        // Policy is only called at creation time, not at login time.
        var policy = Substitute.For<IPasswordPolicy>();
        var service = CreateService(policy: policy);
        await service.EnsureInitializedAsync();

        await service.CreateLocalUserAsync(new CreateUserCommand(
            "u3", "ValidPassword123!", "User Three", "u3@test.local", ["Viewer"]));
        policy.ClearReceivedCalls();

        await service.LoginAsync(new LoginRequest("u3", "ValidPassword123!"));

        policy.DidNotReceive().Validate(Arg.Any<string>());
    }

    // ─── IJwtTokenService delegation ──────────────────────────────────────────

    [Fact]
    public async Task Login_Success_UsesJwtTokenServiceForAccessToken()
    {
        const string syntheticToken = "synthetic-access-token";
        var realJwt = BuildRealJwtService();

        var jwtSub = Substitute.For<IJwtTokenService>();
        jwtSub.GenerateAccessToken(Arg.Any<AuthUser>()).Returns(syntheticToken);
        jwtSub.GenerateRefreshToken().Returns(Convert.ToBase64String(new byte[64]));
        jwtSub.GetValidationParameters().Returns(realJwt.GetValidationParameters());

        var service = CreateService(jwtService: jwtSub);
        await service.EnsureInitializedAsync();

        await service.CreateLocalUserAsync(new CreateUserCommand(
            "u4", "ValidPassword123!", "User Four", "u4@test.local", ["Viewer"]));
        var result = await service.LoginAsync(new LoginRequest("u4", "ValidPassword123!"));

        result.Success.Should().BeTrue();
        result.AccessToken.Should().Be(syntheticToken);
        jwtSub.Received(1).GenerateAccessToken(Arg.Is<AuthUser>(u => u.Username == "u4"));
    }

    [Fact]
    public async Task Login_Success_CallsGenerateRefreshToken()
    {
        var realJwt = BuildRealJwtService();
        var jwtSub = Substitute.For<IJwtTokenService>();
        jwtSub.GenerateAccessToken(Arg.Any<AuthUser>()).Returns("access-token");
        jwtSub.GenerateRefreshToken().Returns(Convert.ToBase64String(new byte[64]));
        jwtSub.GetValidationParameters().Returns(realJwt.GetValidationParameters());

        var service = CreateService(jwtService: jwtSub);
        await service.EnsureInitializedAsync();

        await service.CreateLocalUserAsync(new CreateUserCommand(
            "u5", "ValidPassword123!", "User Five", "u5@test.local", ["Viewer"]));
        await service.LoginAsync(new LoginRequest("u5", "ValidPassword123!"));

        jwtSub.Received(1).GenerateRefreshToken();
    }

    [Fact]
    public async Task RefreshToken_IssuesNewAccessTokenViaJwtService()
    {
        var realJwt = BuildRealJwtService();
        var jwtSub = Substitute.For<IJwtTokenService>();
        jwtSub.GenerateAccessToken(Arg.Any<AuthUser>()).Returns("new-access-token");
        jwtSub.GenerateRefreshToken().Returns(_ => Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        jwtSub.GetValidationParameters().Returns(realJwt.GetValidationParameters());

        var service = CreateService(jwtService: jwtSub);
        await service.EnsureInitializedAsync();

        await service.CreateLocalUserAsync(new CreateUserCommand(
            "u6", "ValidPassword123!", "User Six", "u6@test.local", ["Viewer"]));
        var login = await service.LoginAsync(new LoginRequest("u6", "ValidPassword123!"));
        jwtSub.ClearReceivedCalls();

        var refresh = await service.RefreshAsync(login.RefreshToken!);

        refresh.Should().NotBeNull();
        jwtSub.Received(1).GenerateAccessToken(Arg.Any<AuthUser>());
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private AuthService CreateService(
        IPasswordPolicy? policy = null,
        IJwtTokenService? jwtService = null)
    {
        var opts = Options.Create(new AuthOptions
        {
            JwtSigningKey = TestSigningKey,
            Issuer = "test",
            Audience = "test"
        });

        var users = new AuthUserRepository(_db);
        var roles = new AuthRoleRepository(_db);
        var tokens = new AuthRefreshTokenRepository(_db);

        return new AuthService(
            users,
            roles,
            tokens,
            jwtService ?? BuildRealJwtService(),
            policy ?? new DefaultPasswordPolicy(opts),
            opts,
            NullLogger<AuthService>.Instance);
    }

    private static JwtTokenService BuildRealJwtService()
    {
        var opts = Options.Create(new AuthOptions
        {
            JwtSigningKey = TestSigningKey,
            Issuer = "test",
            Audience = "test"
        });
        return new JwtTokenService(opts, NullLogger<JwtTokenService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
