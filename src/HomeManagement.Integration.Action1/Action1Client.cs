using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeManagement.Integration.Action1.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// Typed HTTP client for the Action1 REST API v1.
/// Base address is configured from <see cref="Action1Options.BaseUrl"/>.
/// Authentication uses <see cref="Action1Options.ApiKey"/> as a Bearer token.
/// </summary>
public sealed class Action1Client
{
    private readonly HttpClient _http;
    private readonly Action1Options _options;
    private readonly ILogger<Action1Client> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
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

    // ── Endpoints ─────────────────────────────────────────────────────────────

    /// <summary>List all managed endpoints in the organization.</summary>
    public async Task<IReadOnlyList<Action1Endpoint>> ListEndpointsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: listing endpoints for org {OrgId}", _options.OrganizationId);

        var response = await _http.GetFromJsonAsync<Action1PagedResponse<Action1Endpoint>>(
            $"v1/organizations/{_options.OrganizationId}/endpoints", JsonOptions, ct);

        return response?.Items ?? [];
    }

    /// <summary>Get a single endpoint by its Action1 endpoint ID.</summary>
    public async Task<Action1Endpoint?> GetEndpointAsync(string endpointId, CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<Action1Endpoint>(
            $"v1/organizations/{_options.OrganizationId}/endpoints/{endpointId}", JsonOptions, ct);
    }

    // ── Patches ───────────────────────────────────────────────────────────────

    /// <summary>Get available patches for a specific endpoint.</summary>
    public async Task<IReadOnlyList<Action1Patch>> GetAvailablePatchesAsync(
        string endpointId, CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching available patches for endpoint {EndpointId}", endpointId);

        var response = await _http.GetFromJsonAsync<Action1PagedResponse<Action1Patch>>(
            $"v1/organizations/{_options.OrganizationId}/endpoints/{endpointId}/patches/available",
            JsonOptions, ct);

        return response?.Items ?? [];
    }

    /// <summary>Get installed software/patches for a specific endpoint.</summary>
    public async Task<IReadOnlyList<Action1SoftwareItem>> GetSoftwareInventoryAsync(
        string endpointId, CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching software inventory for endpoint {EndpointId}", endpointId);

        var response = await _http.GetFromJsonAsync<Action1PagedResponse<Action1SoftwareItem>>(
            $"v1/organizations/{_options.OrganizationId}/endpoints/{endpointId}/software",
            JsonOptions, ct);

        return response?.Items ?? [];
    }

    // ── Deployments ───────────────────────────────────────────────────────────

    /// <summary>
    /// Create a patch deployment for a list of patches on a specific endpoint.
    /// Returns the created deployment ID.
    /// </summary>
    public async Task<string?> CreateDeploymentAsync(
        string endpointId,
        IReadOnlyList<string> patchIds,
        bool allowReboot,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Action1: deploying {Count} patches to endpoint {EndpointId} (allowReboot={AllowReboot})",
            patchIds.Count, endpointId, allowReboot);

        var body = new
        {
            EndpointId = endpointId,
            PatchIds = patchIds,
            AllowReboot = allowReboot
        };

        var resp = await _http.PostAsJsonAsync(
            $"v1/organizations/{_options.OrganizationId}/patches/deploy", body, ct);

        resp.EnsureSuccessStatusCode();

        var created = await resp.Content.ReadFromJsonAsync<Action1Deployment>(JsonOptions, ct);
        return created?.Id;
    }

    /// <summary>Get the current status and results of a deployment.</summary>
    public async Task<Action1Deployment?> GetDeploymentAsync(
        string deploymentId, CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<Action1Deployment>(
            $"v1/organizations/{_options.OrganizationId}/patches/deployments/{deploymentId}",
            JsonOptions, ct);
    }

    /// <summary>
    /// Poll a deployment until it reaches a terminal state (Succeeded/Failed/Cancelled)
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

            if (deployment.Status is "Succeeded" or "Failed" or "Cancelled")
                return deployment;

            _logger.LogDebug("Action1: deployment {Id} still {Status}, polling in {Delay}s",
                deploymentId, deployment.Status, delay.TotalSeconds);

            await Task.Delay(delay, ct);
            // Back off slightly on each poll, max 60s
            if (delay < TimeSpan.FromSeconds(60))
                delay += TimeSpan.FromSeconds(5);
        }

        _logger.LogWarning("Action1: deployment {Id} did not reach terminal state within {Timeout}",
            deploymentId, timeout);
        return null;
    }
}
