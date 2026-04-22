using HomeManagement.Auth;
using Refit;

namespace HomeManagement.Web.Services;

/// <summary>
/// Executes login, refresh, and logout against hm-auth while keeping tokens on the server.
/// </summary>
public interface IWebSessionAuthService
{
    Task<AuthResult> LoginAsync(string username, string password, CancellationToken ct = default);
    Task<bool> RefreshAsync(CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
}

/// <summary>
/// Executes login, refresh, and logout against hm-auth while keeping tokens on the server.
/// </summary>
public sealed class WebSessionAuthService : IWebSessionAuthService
{
    private readonly IAuthApi _authApi;
    private readonly ServerSessionState _sessionState;

    public WebSessionAuthService(IAuthApi authApi, ServerSessionState sessionState)
    {
        _authApi = authApi;
        _sessionState = sessionState;
    }

    public async Task<AuthResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        try
        {
            var result = await _authApi.LoginAsync(new LoginRequest(username, password, AuthProviderType.Local), ct);
            if (result.Success && result.AccessToken is not null && result.RefreshToken is not null)
            {
                _sessionState.SetSession(result.AccessToken, result.RefreshToken);
            }

            return result;
        }
        catch (ApiException ex)
        {
            var errorResult = await ex.GetContentAsAsync<AuthResult>();
            return errorResult ?? new AuthResult(false, Error: ex.Message);
        }
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_sessionState.RefreshToken))
        {
            _sessionState.Clear();
            return false;
        }

        var result = await _authApi.RefreshAsync(new WebRefreshRequest(_sessionState.RefreshToken), ct);
        if (!result.Success || result.AccessToken is null || result.RefreshToken is null)
        {
            _sessionState.Clear();
            return false;
        }

        _sessionState.SetSession(result.AccessToken, result.RefreshToken);
        return true;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_sessionState.RefreshToken))
        {
            try
            {
                await _authApi.RevokeAsync(new WebRevokeRequest(_sessionState.RefreshToken), ct);
            }
            catch
            {
                // Local server-side session clearing is still required even if revoke cannot be sent.
            }
        }

        _sessionState.Clear();
    }
}
