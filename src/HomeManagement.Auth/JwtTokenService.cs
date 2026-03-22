using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HomeManagement.Auth;

/// <summary>
/// Issues and validates JWT access tokens and refresh tokens.
/// </summary>
public sealed class JwtTokenService
{
    private readonly AuthOptions _options;
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validationParameters;
    private readonly ILogger<JwtTokenService> _logger;

    public JwtTokenService(IOptions<AuthOptions> options, ILogger<JwtTokenService> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.JwtSigningKey)
            || _options.JwtSigningKey.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Auth:JwtSigningKey is not configured. " +
                "Generate a cryptographically random 32+ byte key and set it via environment variable or secrets manager.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(_options.JwtSigningKey);
        var securityKey = new SymmetricSecurityKey(keyBytes);
        _signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = securityKey,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Generate an access token for the given user.
    /// </summary>
    public string GenerateAccessToken(AuthUser user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new(JwtRegisteredClaimNames.Name, user.Username),
            new("display_name", user.DisplayName),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("provider", user.Provider.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        foreach (var role in user.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(_options.AccessTokenLifetime),
            signingCredentials: _signingCredentials);

        _logger.LogInformation("Issued access token for user {Username} (roles: {Roles})",
            user.Username, string.Join(", ", user.Roles));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Validate an access token and return its claims principal.
    /// </summary>
    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            return new JwtSecurityTokenHandler().ValidateToken(token, _validationParameters, out _);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            return null;
        }
    }

    /// <summary>
    /// Generate a cryptographically random refresh token.
    /// </summary>
    public static string GenerateRefreshToken()
    {
        return Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
    }

    /// <summary>
    /// Returns the <see cref="TokenValidationParameters"/> for use by ASP.NET Core JWT middleware.
    /// </summary>
    public TokenValidationParameters GetValidationParameters() => _validationParameters;
}
