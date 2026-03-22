using Microsoft.AspNetCore.Components.Authorization;

namespace HomeManagement.Web.Services;

/// <summary>
/// Blazor authentication state provider backed by the server-side circuit session.
/// </summary>
public sealed class AuthStateProvider : AuthenticationStateProvider
{
    private readonly ServerSessionState _sessionState;

    public AuthStateProvider(ServerSessionState sessionState)
    {
        _sessionState = sessionState;
        _sessionState.SessionChanged += OnSessionChanged;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        return Task.FromResult(new AuthenticationState(_sessionState.User));
    }

    private void OnSessionChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
