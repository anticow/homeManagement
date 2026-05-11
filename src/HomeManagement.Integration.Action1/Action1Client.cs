using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeManagement.Integration.Action1.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// Typed HTTP client for the Action1 REST API v3.0.
///
/// Authentication: OAuth2 client_credentials flow.
///   1. POST {BaseUrl}/oauth2/token with client_id + client_secret
///   2. Receive JWT access_token (valid ~1h)
///   3. Send Authorization: Bearer {token} on all subsequent requests
///
/// Tokens are cached and automatically refreshed 2 minutes before expiry.
/// Thread-safe via SemaphoreSlim on the token refresh critical section.
///
/// Base URL is configured from Action1Options.BaseUrl:
///   NA: https://app.action1.com/api/3.0/
///   EU: https://eu.action1.com/api/3.0/
///   AU: https://au.action1.com/api/3.0/
/// </summary>
public sealed class Action1Client : IDisposable
{
    private readonly HttpClient _http;
    private readonly Action1Options _options;
    private readonly ILogger<Action1Client> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTime _tokenExpiresAt = DateTime.MinValue;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Action1Client(
        HttpClient http,
        IOptions<Action1Options> options,
        ILogger<Action1Client> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    // ── OAuth2 token management ────────────────────────────────────────────────

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        // Fast path: existing token is still valid (with 2-minute buffer)
        if (_accessToken is not null && DateTime.UtcNow < _tokenExpiresAt.AddMinutes(-2))
            return _accessToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock
            if (_accessToken is not null && DateTime.UtcNow < _tokenExpiresAt.AddMinutes(-2))
                return _accessToken;

            _logger.LogDebug("Action1: requesting new OAuth2 access token");

            var form = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", _options.ClientId),
                new KeyValuePair<string, string>("client_secret", _options.ClientSecret)
            ]);

            var resp = await _http.PostAsync("oauth2/token", form, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var error = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Action1: OAuth2 token request failed {Status}: {Error}",
                    resp.StatusCode, error);
                throw new InvalidOperationException(
                    $"Action1 authentication failed ({resp.StatusCode}). Check ClientId and ClientSecret.");
            }

            var token = await resp.Content.ReadFromJsonAsync<Action1TokenResponse>(JsonOpts, ct)
                ?? throw new InvalidOperationException("Action1 returned an empty token response.");

            _accessToken = token.AccessToken;
            _tokenExpiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn);

            _logger.LogInformation("Action1: authenticated successfully, token valid for {Seconds}s", token.ExpiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> GetAsync(string relativePath, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, relativePath);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(req, ct);
    }

    private async Task<HttpResponseMessage> PostJsonAsync<T>(string relativePath, T body, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, relativePath);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(body, options: JsonOpts);
        return await _http.SendAsync(req, ct);
    }

    private async Task<IReadOnlyList<T>> GetPagedListAsync<T>(string path, CancellationToken ct)
    {
        var resp = await GetAsync(path, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Action1: {Path} returned 404", path);
            return [];
        }

        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<Action1PagedResponse<T>>(JsonOpts, ct);
        return page?.Items ?? [];
    }

    // ── Endpoints ─────────────────────────────────────────────────────────────

    /// <summary>List all managed endpoints in the organization (with full field set).</summary>
    public async Task<IReadOnlyList<Action1Endpoint>> ListEndpointsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: listing endpoints for org {OrgId}", _options.OrganizationId);
        return await GetPagedListAsync<Action1Endpoint>(
            $"endpoints/managed/{_options.OrganizationId}?fields=*", ct);
    }

    /// <summary>Get a single endpoint by its Action1 endpoint ID.</summary>
    public async Task<Action1Endpoint?> GetEndpointAsync(string endpointId, CancellationToken ct = default)
    {
        var resp = await GetAsync(
            $"endpoints/managed/{_options.OrganizationId}/{endpointId}?fields=*", ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Action1Endpoint>(JsonOpts, ct);
    }

    // ── Patches ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Get missing (available to install) patches for a specific endpoint.
    ///
    /// NOTE: Action1's v3.0 API does not publicly document a per-endpoint patch list endpoint.
    /// This uses the anticipated path based on the endpoint detail response fields.
    /// The method returns an empty list (not an error) if the path is not found,
    /// so the integration degrades gracefully while the patch count is still available
    /// via ListEndpointsAsync (missing_critical_updates / missing_other_updates fields).
    /// </summary>
    public async Task<IReadOnlyList<Action1Patch>> GetAvailablePatchesAsync(
        string endpointId, CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching available patches for endpoint {EndpointId}", endpointId);

        // Attempt the most likely path; returns [] on 404 so callers degrade gracefully
        return await GetPagedListAsync<Action1Patch>(
            $"endpoints/managed/{_options.OrganizationId}/{endpointId}/patches/missing", ct);
    }

    /// <summary>Get installed software inventory for a specific endpoint.</summary>
    public async Task<IReadOnlyList<Action1SoftwareItem>> GetSoftwareInventoryAsync(
        string endpointId, CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching software inventory for endpoint {EndpointId}", endpointId);
        return await GetPagedListAsync<Action1SoftwareItem>(
            $"endpoints/managed/{_options.OrganizationId}/{endpointId}/software", ct);
    }

    // ── Deployments ───────────────────────────────────────────────────────────

    /// <summary>
    /// Create a patch deployment (install request) for selected patches on an endpoint.
    /// Returns the created deployment ID, or null if the request failed.
    ///
    /// NOTE: The exact Action1 API path for initiating patch installs is not in their
    /// public documentation. Adjust the path below if Action1 support provides a different URL.
    /// </summary>
    public async Task<string?> CreateDeploymentAsync(
        string endpointId,
        IReadOnlyList<string> patchIds,
        bool allowReboot,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Action1: deploying {Count} patches to endpoint {EndpointId} (allowReboot={AllowReboot})",
            patchIds.Count, endpointId, allowReboot);

        var body = new
        {
            endpoint_id = endpointId,
            patch_ids = patchIds,
            allow_reboot = allowReboot
        };

        var resp = await PostJsonAsync(
            $"endpoints/managed/{_options.OrganizationId}/patches/install", body, ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Action1: deployment request returned {Status} for endpoint {EndpointId}",
                resp.StatusCode, endpointId);
            return null;
        }

        var created = await resp.Content.ReadFromJsonAsync<Action1DeploymentCreated>(JsonOpts, ct);
        return created?.Id;
    }

    /// <summary>Get the current status of a deployment.</summary>
    public async Task<Action1Deployment?> GetDeploymentAsync(
        string deploymentId, CancellationToken ct = default)
    {
        var resp = await GetAsync(
            $"endpoints/managed/{_options.OrganizationId}/patches/deployments/{deploymentId}", ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<Action1Deployment>(JsonOpts, ct);
    }

    /// <summary>
    /// Poll a deployment until it reaches a terminal state (Succeeded/Failed/Cancelled/Completed)
    /// or until <paramref name="timeout"/> elapses.
    /// </summary>
    public async Task<Action1Deployment?> PollDeploymentUntilCompleteAsync(
        string deploymentId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var deployment = await GetDeploymentAsync(deploymentId, ct);
            if (deployment is null) return null;

            if (deployment.Status is "Succeeded" or "Failed" or "Cancelled" or "Completed")
                return deployment;

            _logger.LogDebug("Action1: deployment {Id} still {Status}, polling in {Delay}s",
                deploymentId, deployment.Status, delay.TotalSeconds);

            await Task.Delay(delay, ct);
            if (delay < TimeSpan.FromSeconds(60))
                delay += TimeSpan.FromSeconds(5);
        }

        _logger.LogWarning("Action1: deployment {Id} did not complete within {Timeout}",
            deploymentId, timeout);
        return null;
    }

    public void Dispose() => _tokenLock.Dispose();
}
