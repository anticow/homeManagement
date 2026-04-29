using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Web.Services;

/// <summary>
/// Client for the Auth service's admin endpoints, forwarding the current session token.
/// </summary>
public interface IAdminApi
{
    Task<IReadOnlyList<AuthUser>> GetUsersAsync(CancellationToken ct = default);
}

public sealed class AdminApiClient : IAdminApi
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ServerSessionState _sessionState;

    public AdminApiClient(IHttpClientFactory httpClientFactory, ServerSessionState sessionState)
    {
        _httpClientFactory = httpClientFactory;
        _sessionState = sessionState;
    }

    public const string HttpClientName = "AuthAdminApi";

    public async Task<IReadOnlyList<AuthUser>> GetUsersAsync(CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        if (!string.IsNullOrWhiteSpace(_sessionState.AccessToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _sessionState.AccessToken);
        }

        var response = await client.GetAsync("/api/admin/users", ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<AuthUser>>(ct) ?? [];
    }
}
