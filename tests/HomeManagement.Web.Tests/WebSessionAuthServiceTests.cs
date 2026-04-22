using FluentAssertions;
using HomeManagement.Auth;
using HomeManagement.Web.Services;
using NSubstitute;

namespace HomeManagement.Web.Tests;

public sealed class WebSessionAuthServiceTests
{
    [Fact]
    public async Task LoginAsync_Success_PopulatesSession()
    {
        var authApi = Substitute.For<IAuthApi>();
        var session = new ServerSessionState();
        var service = new WebSessionAuthService(authApi, session);

        authApi.LoginAsync(Arg.Any<LoginRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AuthResult(
                true,
                AccessToken: CreateToken("alice", ["Admin"]),
                RefreshToken: "refresh-a"));

        var result = await service.LoginAsync("alice", "Password123!");

        result.Success.Should().BeTrue();
        session.IsAuthenticated.Should().BeTrue();
        session.User.Identity!.Name.Should().Be("alice");
    }

    [Fact]
    public async Task RefreshAsync_Failure_ClearsSession()
    {
        var authApi = Substitute.For<IAuthApi>();
        var session = new ServerSessionState();
        session.SetSession(CreateToken("alice", ["Admin"]), "refresh-a");
        var service = new WebSessionAuthService(authApi, session);

        authApi.RefreshAsync(Arg.Any<WebRefreshRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AuthResult(false, Error: "expired"));

        var refreshed = await service.RefreshAsync();

        refreshed.Should().BeFalse();
        session.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task LogoutAsync_RevokesRefreshTokenAndClearsSession()
    {
        var authApi = Substitute.For<IAuthApi>();
        var session = new ServerSessionState();
        session.SetSession(CreateToken("alice", ["Admin"]), "refresh-a");
        var service = new WebSessionAuthService(authApi, session);

        await service.LogoutAsync();

        await authApi.Received(1).RevokeAsync(
            Arg.Is<WebRevokeRequest>(request => request.RefreshToken == "refresh-a"),
            Arg.Any<CancellationToken>());
        session.IsAuthenticated.Should().BeFalse();
    }

    private static string CreateToken(string username, IReadOnlyList<string> roles)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.Name, username)
        };

        claims.AddRange(roles.Select(role => new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)));

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15));

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}
