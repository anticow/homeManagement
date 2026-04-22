using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace HomeManagement.Web.Services;

/// <summary>
/// Blazor authentication state provider backed by the server-side circuit session.
/// Persists tokens in the browser's protected session storage so a page refresh
/// does not force the user to log in again.
/// </summary>
public sealed class AuthStateProvider : AuthenticationStateProvider
{
    private const string StorageKey = "hm_session";
    private readonly ServerSessionState _sessionState;
    private readonly ProtectedSessionStorage _storage;
    private bool _initialized;

    public AuthStateProvider(ServerSessionState sessionState, ProtectedSessionStorage storage)
    {
        _sessionState = sessionState;
        _storage = storage;
        _sessionState.SessionChanged += OnSessionChanged;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            await TryRestoreSessionAsync();
        }

        return new AuthenticationState(_sessionState.User);
    }

    /// <summary>
    /// Persist the current tokens to browser session storage.
    /// </summary>
    public async Task PersistSessionAsync()
    {
        if (_sessionState.IsAuthenticated)
        {
            var data = new StoredSession(_sessionState.AccessToken!, _sessionState.RefreshToken!);
            await _storage.SetAsync(StorageKey, data);
        }
    }

    /// <summary>
    /// Remove persisted tokens from browser session storage.
    /// </summary>
    public async Task ClearPersistedSessionAsync()
    {
        await _storage.DeleteAsync(StorageKey);
    }

    private async Task TryRestoreSessionAsync()
    {
        try
        {
            var result = await _storage.GetAsync<StoredSession>(StorageKey);
            if (result.Success && result.Value is { } session
                && !string.IsNullOrWhiteSpace(session.AccessToken)
                && !string.IsNullOrWhiteSpace(session.RefreshToken))
            {
                _sessionState.SetSession(session.AccessToken, session.RefreshToken);
            }
        }
        catch
        {
            // Storage may be unavailable during prerendering — ignore.
        }
    }

    private void OnSessionChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    private sealed record StoredSession(string AccessToken, string RefreshToken);
}
