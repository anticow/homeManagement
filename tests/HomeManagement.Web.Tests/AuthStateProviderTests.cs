using System.Security.Claims;
using FluentAssertions;
using HomeManagement.Web.Services;

namespace HomeManagement.Web.Tests;

/// <summary>
/// Tests for <see cref="AuthStateProvider"/> — Blazor authentication state management.
/// </summary>
public sealed class AuthStateProviderTests
{
    // ── Initial state ──

    [Fact]
    public async Task GetAuthenticationStateAsync_Initial_UserIsNotAuthenticated()
    {
        var provider = new AuthStateProvider(new ServerSessionState());

        var state = await provider.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    // ── Session-backed state ──

    [Fact]
    public async Task SessionSet_UserBecomesAuthenticated()
    {
        var session = new ServerSessionState();
        var provider = new AuthStateProvider(session);

        session.SetSession(CreateToken("alice", ["Admin"]), "refresh-a");
        var state = await provider.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task SessionSet_SetsCorrectUsername()
    {
        var session = new ServerSessionState();
        var provider = new AuthStateProvider(session);

        session.SetSession(CreateToken("bob", ["Viewer"]), "refresh-a");
        var state = await provider.GetAuthenticationStateAsync();

        state.User.Identity!.Name.Should().Be("bob");
    }

    [Fact]
    public async Task SessionSet_SetsCorrectAuthenticationType()
    {
        var session = new ServerSessionState();
        var provider = new AuthStateProvider(session);

        session.SetSession(CreateToken("charlie", ["Operator"]), "refresh-a");
        var state = await provider.GetAuthenticationStateAsync();

        state.User.Identity!.AuthenticationType.Should().Be("server-session");
    }

    [Fact]
    public async Task SessionSet_SingleRole_HasRoleClaim()
    {
        var session = new ServerSessionState();
        var provider = new AuthStateProvider(session);

        session.SetSession(CreateToken("dave", ["Admin"]), "refresh-a");
        var state = await provider.GetAuthenticationStateAsync();

        state.User.IsInRole("Admin").Should().BeTrue();
    }

    [Fact]
    public async Task SessionSet_MultipleRoles_HasAllRoleClaims()
    {
        var session = new ServerSessionState();
        var provider = new AuthStateProvider(session);

        session.SetSession(CreateToken("eve", ["Viewer", "Auditor"]), "refresh-a");
        var state = await provider.GetAuthenticationStateAsync();

        state.User.IsInRole("Viewer").Should().BeTrue();
        state.User.IsInRole("Auditor").Should().BeTrue();
    }

    [Fact]
    public async Task SessionSet_NoRoles_StillAuthenticated()
    {
        var session = new ServerSessionState();
        var provider = new AuthStateProvider(session);

        session.SetSession(CreateToken("frank", []), "refresh-a");
        var state = await provider.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeTrue();
        state.User.Claims.Where(c => c.Type == ClaimTypes.Role).Should().BeEmpty();
    }

    [Fact]
    public async Task SessionSet_CalledTwice_OverridesPreviousUser()
    {
        var session = new ServerSessionState();
        var provider = new AuthStateProvider(session);

        session.SetSession(CreateToken("old-user", ["Admin"]), "refresh-a");
        session.SetSession(CreateToken("new-user", ["Viewer"]), "refresh-b");
        var state = await provider.GetAuthenticationStateAsync();

        state.User.Identity!.Name.Should().Be("new-user");
        state.User.IsInRole("Admin").Should().BeFalse();
        state.User.IsInRole("Viewer").Should().BeTrue();
    }

    // ── ClearAuthenticationState ──

    [Fact]
    public async Task ClearAuthenticationState_AfterLogin_UserIsNoLongerAuthenticated()
    {
        var session = new ServerSessionState();
        var provider = new AuthStateProvider(session);
        session.SetSession(CreateToken("alice", ["Admin"]), "refresh-a");

        session.Clear();
        var state = await provider.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task ClearAuthenticationState_WhenAlreadyClear_DoesNotThrow()
    {
        var session = new ServerSessionState();

        var act = () =>
        {
            session.Clear();
            return Task.CompletedTask;
        };

        await act.Should().NotThrowAsync();
    }

    // ── Notification ──

    [Fact]
    public async Task SetAuthenticatedUser_NotifiesStateChanged()
    {
        var session = new ServerSessionState();
        var provider = new AuthStateProvider(session);
        var notifications = 0;
        provider.AuthenticationStateChanged += _ =>
        {
            notifications++;
            return;
        };

        session.SetSession(CreateToken("alice", ["Viewer"]), "refresh-a");

        // Give the event a moment to fire
        await Task.Delay(50);
        notifications.Should().Be(1);
    }

    [Fact]
    public async Task ClearAuthenticationState_NotifiesStateChanged()
    {
        var session = new ServerSessionState();
        session.SetSession(CreateToken("alice", ["Admin"]), "refresh-a");
        var provider = new AuthStateProvider(session);
        var notifications = 0;
        provider.AuthenticationStateChanged += _ =>
        {
            notifications++;
            return;
        };

        session.Clear();

        await Task.Delay(50);
        notifications.Should().Be(1);
    }

    private static string CreateToken(string username, IReadOnlyList<string> roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, username) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15));

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }
}
