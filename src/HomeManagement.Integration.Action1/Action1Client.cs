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

    // ── Patches / missing updates ─────────────────────────────────────────────

    /// <summary>
    /// Get missing updates for a specific endpoint.
    ///
    /// Real Action1 API: GET /updates/{orgId}?endpoint_id={endpointId}&amp;fields=*
    ///
    /// Returns an empty list on 404 so callers degrade gracefully.
    /// Each item includes a <see cref="PatchToInstall.Version"/> which is required
    /// when creating a deployment policy instance.
    /// </summary>
    public async Task<IReadOnlyList<Action1Patch>> GetAvailablePatchesAsync(
        string endpointId, CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching available updates for endpoint {EndpointId}", endpointId);
        return await GetPagedListAsync<Action1Patch>(
            $"updates/{_options.OrganizationId}?endpoint_id={Uri.EscapeDataString(endpointId)}&fields=*", ct);
    }

    /// <summary>Get installed software inventory for a specific endpoint.</summary>
    public async Task<IReadOnlyList<Action1SoftwareItem>> GetSoftwareInventoryAsync(
        string endpointId, CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching software inventory for endpoint {EndpointId}", endpointId);
        return await GetPagedListAsync<Action1SoftwareItem>(
            $"apps/{_options.OrganizationId}/data/{endpointId}", ct);
    }

    // ── Installed updates & compliance ────────────────────────────────────────

    /// <summary>
    /// Fetch installed update history for the organization (or optionally filtered to one endpoint).
    ///
    /// Real Action1 API: GET /updates/installed/{orgId}?fields=*
    ///   Optional filter: &amp;endpoint_id={endpointId}
    ///
    /// Returns an empty list on 404 (endpoint not found or no history yet).
    /// Use <see cref="GetLastPatchedDatesAsync"/> for a pre-aggregated map.
    /// </summary>
    public async Task<IReadOnlyList<Action1InstalledUpdate>> GetInstalledUpdatesAsync(
        string? endpointId = null, CancellationToken ct = default)
    {
        var path = $"updates/installed/{_options.OrganizationId}?fields=*";
        if (!string.IsNullOrEmpty(endpointId))
            path += $"&endpoint_id={Uri.EscapeDataString(endpointId)}";

        _logger.LogDebug("Action1: fetching installed updates for org {OrgId} (endpoint={EndpointId})",
            _options.OrganizationId, endpointId ?? "ALL");

        return await GetPagedListAsync<Action1InstalledUpdate>(path, ct);
    }

    /// <summary>
    /// Returns a dictionary of endpointId → most recent install date by fetching
    /// all installed updates for the org in one call and aggregating.
    ///
    /// Results are cached per call (not across calls). Callers on the fleet page
    /// should call this once and share the result to avoid repeated API calls.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTime>> GetLastPatchedDatesAsync(
        CancellationToken ct = default)
    {
        var allInstalled = await GetInstalledUpdatesAsync(null, ct);
        return allInstalled
            .Where(u => u.EndpointId is not null && u.InstallDate.HasValue)
            .GroupBy(u => u.EndpointId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Max(u => u.InstallDate!.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    // ── CVE / Vulnerabilities ─────────────────────────────────────────────────

    /// <summary>
    /// Fetch CVEs with available remediations in Action1.
    ///
    /// Real Action1 API: GET /Vulnerabilities/{orgId}?fields=*
    ///
    /// Action1 correlates NVD CVEs with packages in its software repository.
    /// Returns an empty list on 404.
    /// </summary>
    public async Task<IReadOnlyList<Action1Vulnerability>> GetVulnerabilitiesAsync(
        CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching vulnerabilities for org {OrgId}", _options.OrganizationId);
        return await GetPagedListAsync<Action1Vulnerability>(
            $"Vulnerabilities/{_options.OrganizationId}?fields=*", ct);
    }

    // ── Policy instance deployment (approve & install) ────────────────────────

    /// <summary>
    /// Create a one-time patch deployment by posting a policy instance to Action1.
    ///
    /// Real Action1 API: POST /policies/instances/{orgId}
    ///
    /// Action1 has no standalone "approve patch" endpoint. Approval is achieved by
    /// creating a policy instance with template_id="deploy_update", scope="Specified",
    /// update_approval="auto", and the specific packages listed. This runs immediately.
    ///
    /// Returns the created policy instance ID, or null if the request failed.
    /// </summary>
    public async Task<string?> CreateDeploymentAsync(
        string endpointId,
        IReadOnlyList<PatchToInstall> patches,
        bool allowReboot,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Action1: creating deployment policy for {Count} patches on endpoint {EndpointId} (allowReboot={AllowReboot})",
            patches.Count, endpointId, allowReboot);

        // Build the packages dict: [{"pkg_id": "version"}, ...] — one object per patch.
        // Action1 API uses an array of single-key objects, not a standard map.
        var packageList = patches.Select(p =>
            new Dictionary<string, string> { [p.Id] = p.Version ?? "latest" }
        ).ToList();

        var body = new
        {
            name = $"homeManagement: deploy {patches.Count} update(s) on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC",
            retry_minutes = 0,
            endpoints = new[] { new { id = endpointId, type = "Endpoint" } },
            actions = new[]
            {
                new
                {
                    name = "Deploy Update",
                    template_id = "deploy_update",
                    @params = new
                    {
                        display_summary = $"Approved via homeManagement ({patches.Count} update(s))",
                        packages = packageList,
                        update_approval = "auto",
                        scope = "Specified",
                        reboot_options = new
                        {
                            auto_reboot = allowReboot ? "yes" : "no",
                            show_message = "yes",
                            message_text = "System maintenance is in progress. Please save your work and allow the system to reboot.",
                            timeout = 240
                        }
                    }
                }
            }
        };

        var resp = await PostJsonAsync($"policies/instances/{_options.OrganizationId}", body, ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Action1: deployment policy creation returned {Status} for endpoint {EndpointId}",
                resp.StatusCode, endpointId);
            return null;
        }

        var created = await resp.Content.ReadFromJsonAsync<Action1PolicyInstance>(JsonOpts, ct);
        _logger.LogInformation("Action1: policy instance {Id} created for endpoint {EndpointId}",
            created?.Id, endpointId);
        return created?.Id;
    }

    /// <summary>
    /// Overload for callers that only have patch IDs (no version).
    /// Fetches the current available patch list to resolve versions, then delegates.
    /// </summary>
    public async Task<string?> CreateDeploymentAsync(
        string endpointId,
        IReadOnlyList<string> patchIds,
        bool allowReboot,
        CancellationToken ct = default)
    {
        // Resolve versions from the live missing-updates list so the deployment body is complete.
        var available = await GetAvailablePatchesAsync(endpointId, ct);
        var versionLookup = available.ToDictionary(p => p.Id, p => p.Version, StringComparer.OrdinalIgnoreCase);

        var items = patchIds.Select(id =>
            new PatchToInstall(id, versionLookup.GetValueOrDefault(id))).ToList();

        return await CreateDeploymentAsync(endpointId, items, allowReboot, ct);
    }

    /// <summary>
    /// Get the current status of a deployment (policy instance).
    ///
    /// Real Action1 API: GET /policies/instances/{orgId}/{instanceId}
    /// Returns null if the instance is not found.
    /// </summary>
    public async Task<Action1Deployment?> GetDeploymentAsync(
        string deploymentId, CancellationToken ct = default)
    {
        var resp = await GetAsync(
            $"policies/instances/{_options.OrganizationId}/{deploymentId}", ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();
        var instance = await resp.Content.ReadFromJsonAsync<Action1PolicyInstance>(JsonOpts, ct);
        if (instance is null) return null;

        // Map policy instance to legacy Action1Deployment so existing callers aren't broken.
        return new Action1Deployment(
            Id: instance.Id,
            Status: instance.Status,
            Results: []);
    }

    /// <summary>
    /// Poll a deployment (policy instance) until it reaches a terminal state
    /// (Completed/Failed/Disabled) or until <paramref name="timeout"/> elapses.
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

            // Policy instance terminal statuses
            if (deployment.Status is "Completed" or "Failed" or "Disabled" or "Succeeded" or "Cancelled")
                return deployment;

            _logger.LogDebug("Action1: policy instance {Id} still {Status}, polling in {Delay}s",
                deploymentId, deployment.Status, delay.TotalSeconds);

            await Task.Delay(delay, ct);
            if (delay < TimeSpan.FromSeconds(60))
                delay += TimeSpan.FromSeconds(5);
        }

        _logger.LogWarning("Action1: policy instance {Id} did not complete within {Timeout}",
            deploymentId, timeout);
        return null;
    }

    public void Dispose() => _tokenLock.Dispose();
}
