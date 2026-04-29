using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeManagement.Auth.Tests;

/// <summary>
/// Tests for <see cref="JwtTokenService"/> — JWT generation, validation, and refresh tokens.
/// </summary>
public sealed class JwtTokenServiceTests
{
    private const string TestSigningKey = "this-is-a-test-signing-key-that-is-long-enough-for-hmac-sha256!";

    private static JwtTokenService CreateService(AuthOptions? options = null)
    {
        var opts = options ?? new AuthOptions
        {
            JwtSigningKey = TestSigningKey,
            Issuer = "test-issuer",
            Audience = "test-audience",
            AccessTokenLifetime = TimeSpan.FromMinutes(15)
        };

        return new JwtTokenService(
            Options.Create(opts),
            NullLogger<JwtTokenService>.Instance);
    }

    private static AuthUser CreateTestUser(
        string username = "testuser",
        IReadOnlyList<string>? roles = null)
    {
        return new AuthUser(
            Guid.NewGuid(),
            username,
            "Test User",
            "test@corp.local",
            AuthProviderType.Local,
            roles ?? ["Operator"],
            DateTime.UtcNow.AddDays(-30),
            DateTime.UtcNow);
    }

    // ── Token generation ──

    [Fact]
    public void GenerateAccessToken_ValidUser_ReturnsNonEmptyToken()
    {
        var svc = CreateService();
        var token = svc.GenerateAccessToken(CreateTestUser());

        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GenerateAccessToken_ValidUser_ContainsExpectedClaims()
    {
        var svc = CreateService();
        var user = CreateTestUser("alice", ["Admin", "Operator"]);

        var tokenString = svc.GenerateAccessToken(user);
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        jwt.Issuer.Should().Be("test-issuer");
        jwt.Audiences.Should().Contain("test-audience");
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == user.UserId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Name && c.Value == "alice");
        jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Email && c.Value == "test@corp.local");
        jwt.Claims.Should().Contain(c => c.Type == "provider" && c.Value == "Local");
        jwt.Claims.Should().Contain(c => c.Type == "display_name" && c.Value == "Test User");
    }

    [Fact]
    public void GenerateAccessToken_MultipleRoles_AllRolesPresent()
    {
        var svc = CreateService();
        var user = CreateTestUser(roles: ["Admin", "Auditor"]);

        var tokenString = svc.GenerateAccessToken(user);
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        var roleClaims = jwt.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
        roleClaims.Should().Contain("Admin");
        roleClaims.Should().Contain("Auditor");
    }

    [Fact]
    public void GenerateAccessToken_SetsCorrectExpiry()
    {
        var svc = CreateService(new AuthOptions
        {
            JwtSigningKey = TestSigningKey,
            Issuer = "test",
            Audience = "test",
            AccessTokenLifetime = TimeSpan.FromMinutes(30)
        });

        var tokenString = svc.GenerateAccessToken(CreateTestUser());
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        jwt.ValidTo.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(30), precision: TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void GenerateAccessToken_EachTokenHasUniqueJti()
    {
        var svc = CreateService();
        var user = CreateTestUser();

        var token1 = svc.GenerateAccessToken(user);
        var token2 = svc.GenerateAccessToken(user);

        var handler = new JwtSecurityTokenHandler();
        var jti1 = handler.ReadJwtToken(token1).Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        var jti2 = handler.ReadJwtToken(token2).Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;

        jti1.Should().NotBe(jti2);
    }

    // ── Token validation ──

    [Fact]
    public void ValidateToken_ValidToken_ReturnsPrincipal()
    {
        var svc = CreateService();
        var tokenString = svc.GenerateAccessToken(CreateTestUser("bob"));

        var principal = svc.ValidateToken(tokenString);

        principal.Should().NotBeNull();
        principal!.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void ValidateToken_ValidToken_PrincipalContainsUsername()
    {
        var svc = CreateService();
        var tokenString = svc.GenerateAccessToken(CreateTestUser("charlie"));

        var principal = svc.ValidateToken(tokenString);

        principal!.FindFirst(JwtRegisteredClaimNames.Name)!.Value.Should().Be("charlie");
    }

    [Fact]
    public void ValidateToken_TamperedToken_ReturnsNull()
    {
        var svc = CreateService();
        var tokenString = svc.GenerateAccessToken(CreateTestUser());

        var tampered = tokenString + "x";

        svc.ValidateToken(tampered).Should().BeNull();
    }

    [Fact]
    public void ValidateToken_WrongSigningKey_ReturnsNull()
    {
        var svc1 = CreateService(new AuthOptions
        {
            JwtSigningKey = TestSigningKey,
            Issuer = "test",
            Audience = "test"
        });
        var svc2 = CreateService(new AuthOptions
        {
            JwtSigningKey = "a-completely-different-key-that-is-long-enough-for-sha256!",
            Issuer = "test",
            Audience = "test"
        });

        var tokenString = svc1.GenerateAccessToken(CreateTestUser());
        svc2.ValidateToken(tokenString).Should().BeNull();
    }

    [Fact]
    public void ValidateToken_ExpiredToken_ReturnsNull()
    {
        var svc = CreateService(new AuthOptions
        {
            JwtSigningKey = TestSigningKey,
            Issuer = "test",
            Audience = "test",
            AccessTokenLifetime = TimeSpan.FromSeconds(1)
        });

        var tokenString = svc.GenerateAccessToken(CreateTestUser());

        // Wait for the token to expire
        Thread.Sleep(2000);

        // Validate with zero clock skew to confirm the token is truly expired
        var parameters = svc.GetValidationParameters();
        parameters.ClockSkew = TimeSpan.Zero;

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        ClaimsPrincipal? result = null;
        try
        {
            result = handler.ValidateToken(tokenString, parameters, out _);
        }
        catch (Microsoft.IdentityModel.Tokens.SecurityTokenException)
        {
            // expected
        }

        result.Should().BeNull();
    }

    // ── Refresh tokens ──

    [Fact]
    public void GenerateRefreshToken_ReturnsBase64String()
    {
        var svc = CreateService();
        var token = svc.GenerateRefreshToken();

        token.Should().NotBeNullOrEmpty();
        // Should be valid Base64
        var bytes = Convert.FromBase64String(token);
        bytes.Length.Should().Be(64);
    }

    [Fact]
    public void GenerateRefreshToken_EachCallReturnsUniqueToken()
    {
        var svc = CreateService();
        var token1 = svc.GenerateRefreshToken();
        var token2 = svc.GenerateRefreshToken();

        token1.Should().NotBe(token2);
    }

    // ── Validation parameters ──

    [Fact]
    public void GetValidationParameters_ReturnsConfiguredParameters()
    {
        var svc = CreateService();
        var params_ = svc.GetValidationParameters();

        params_.ValidateIssuer.Should().BeTrue();
        params_.ValidIssuer.Should().Be("test-issuer");
        params_.ValidateAudience.Should().BeTrue();
        params_.ValidAudience.Should().Be("test-audience");
        params_.ValidateLifetime.Should().BeTrue();
        params_.ValidateIssuerSigningKey.Should().BeTrue();
    }
}
