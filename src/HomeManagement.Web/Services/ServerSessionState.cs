using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace HomeManagement.Web.Services;

/// <summary>
/// Holds the authenticated Blazor Server circuit session state.
/// </summary>
public sealed class ServerSessionState
{
    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(AccessToken);
    public ClaimsPrincipal User { get; private set; } = new(new ClaimsIdentity());
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public DateTimeOffset? AccessTokenExpiresUtc { get; private set; }

    public event Action? SessionChanged;

    public void SetSession(string accessToken, string refreshToken)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        User = new ClaimsPrincipal(new ClaimsIdentity(token.Claims, "server-session"));
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        AccessTokenExpiresUtc = token.ValidTo == DateTime.MinValue
            ? null
            : new DateTimeOffset(token.ValidTo, TimeSpan.Zero);

        SessionChanged?.Invoke();
    }

    public void Clear()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity());
        AccessToken = null;
        RefreshToken = null;
        AccessTokenExpiresUtc = null;
        SessionChanged?.Invoke();
    }
}
