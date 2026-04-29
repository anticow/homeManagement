using System.Security.Claims;
using HomeManagement.Abstractions.Models;
using Microsoft.IdentityModel.Tokens;

namespace HomeManagement.Auth;

/// <summary>
/// Issues and validates JWT access tokens and generates opaque refresh tokens.
/// </summary>
public interface IJwtTokenService
{
    string GenerateAccessToken(AuthUser user);
    ClaimsPrincipal? ValidateToken(string token);
    TokenValidationParameters GetValidationParameters();
    string GenerateRefreshToken();
}
