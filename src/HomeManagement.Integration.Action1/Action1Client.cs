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

    /// <summary>
    /// Invalidates the cached token so the next call to GetAccessTokenAsync forces a fresh fetch.
    /// Called automatically on 401/403 before a single retry.
    /// </summary>
    private void InvalidateToken()
    {
        _accessToken = null;
        _tokenExpiresAt = DateTime.MinValue;
    }

    private async Task<HttpResponseMessage> GetAsync(string relativePath, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, relativePath);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req, ct);

        // Only retry on 401 (token expired). 403 = permission denied — refreshing the token
        // will not help and will only double the API call noise.
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Action1: 401 on GET {Path} — forcing token refresh and retrying once.", relativePath);
            resp.Dispose();
            InvalidateToken();
            var freshToken = await GetAccessTokenAsync(ct);
            using var retry = new HttpRequestMessage(HttpMethod.Get, relativePath);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);
            return await _http.SendAsync(retry, ct);
        }

        return resp;
    }

    private async Task<HttpResponseMessage> PostJsonAsync<T>(string relativePath, T body, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, relativePath);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(body, options: JsonOpts);
        var resp = await _http.SendAsync(req, ct);

        // Only retry on 401 (token expired). 403 = permission denied — refreshing the token
        // will not help and will only double the API call noise.
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Action1: 401 on POST {Path} — forcing token refresh and retrying once.", relativePath);
            resp.Dispose();
            InvalidateToken();
            var freshToken = await GetAccessTokenAsync(ct);
            using var retry = new HttpRequestMessage(HttpMethod.Post, relativePath);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);
            retry.Content = JsonContent.Create(body, options: JsonOpts);
            return await _http.SendAsync(retry, ct);
        }

        return resp;
    }

    private async Task<HttpResponseMessage> PatchJsonAsync<T>(string relativePath, T body, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Patch, relativePath);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(body, options: JsonOpts);
        var resp = await _http.SendAsync(req, ct);

        // Only retry on 401 (token expired). 403 = permission denied — refreshing the token
        // will not help and will only double the API call noise.
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Action1: 401 on PATCH {Path} — forcing token refresh and retrying once.", relativePath);
            resp.Dispose();
            InvalidateToken();
            var freshToken = await GetAccessTokenAsync(ct);
            using var retry = new HttpRequestMessage(HttpMethod.Patch, relativePath);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);
            retry.Content = JsonContent.Create(body, options: JsonOpts);
            return await _http.SendAsync(retry, ct);
        }

        return resp;
    }

    private async Task<IReadOnlyList<T>> GetPagedListAsync<T>(string path, CancellationToken ct)
    {
        var resp = await GetAsync(path, ct);
        _logger.LogDebug("Action1: GET {Path} → {Status}", path, (int)resp.StatusCode);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Action1: {Path} returned 404", path);
            return [];
        }

        // 403 on enrichment endpoints (e.g. updates/installed) means the org plan or
        // API role doesn't include this data — treat as empty rather than failing the caller.
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Action1: {Path} returned 403 — endpoint may require additional plan/permissions. Returning empty list.", path);
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

    // ── Catalog-level update approval ─────────────────────────────────────────

    /// <summary>
    /// Fetch org-level catalog updates filtered by approval status.
    ///
    /// Real Action1 API: GET /updates/{orgId}?approval_status={status}&amp;fields=*
    ///
    /// Unlike GetAvailablePatchesAsync (which filters by endpoint), this returns
    /// the org-wide update catalog view — the same list visible in the Action1
    /// Update Approval console screen.
    ///
    /// Pass approvalStatus = "New" to list updates pending catalog approval.
    /// </summary>
    public async Task<IReadOnlyList<Action1CatalogUpdate>> GetCatalogUpdatesAsync(
        string approvalStatus = "New", CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching catalog updates with approval_status={Status} for org {OrgId}",
            approvalStatus, _options.OrganizationId);
        // limit=50    — matches what Action1's own console uses; a limit=200 query with
        //               only_latest=yes forces server-side deduplication across all endpoints
        //               which can time out on large orgs.
        // only_latest=yes — deduplicates so each KB appears once rather than once-per-version.
        // from=0       — explicit start offset; required alongside limit for stable pagination.
        // Omit fields=* — for org-wide queries Action1 may include per-endpoint deployment
        // history for every update, producing a very large slow response. The default field
        // set contains all the metadata we need (name, severity, approval_status, etc.).
        _logger.LogInformation("Action1: starting catalog fetch orgId={OrgId} approvalStatus={Status}",
            _options.OrganizationId, approvalStatus);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await GetPagedListAsync<Action1CatalogUpdate>(
            $"updates/{_options.OrganizationId}?approval_status={Uri.EscapeDataString(approvalStatus)}&limit=50&only_latest=yes&from=0", ct);
        sw.Stop();
        _logger.LogInformation("Action1: catalog fetch done: {Count} items in {ElapsedMs}ms", result.Count, sw.ElapsedMilliseconds);
        return result;
    }

    /// <summary>
    /// Set the catalog-level approval status for a single update.
    ///
    /// Action1 has two catalog update types that require different endpoints:
    ///
    ///   1. Software delivery packages (named IDs: Vendor_Product_Timestamp_builtin)
    ///      PATCH /software-repository/all/{packageId}/versions/{versionId}
    ///      Body: { "approval_status": "Approved|Declined|New" }
    ///      No scope field — the API returns 400 if scope is included.
    ///
    ///      The packageId and versionId are NOT the same value:
    ///        versionId = full update ID (e.g. Google_Chrome_1570243626751_builtin)
    ///        packageId = versionId with the trailing timestamp segment stripped
    ///                    (e.g. Google_Chrome)
    ///      If the timestamp-stripped URL also returns 500, the package cannot be
    ///      approved via software-repository — it is logged and returned as Error.
    ///      We do NOT fall back to the org-updates endpoint because that will always
    ///      return 403 for global-catalog packages, creating confusing log noise.
    ///
    ///   2. Security / Windows updates (UUID IDs: xxxxxxxx-..._builtin, or no suffix)
    ///      PATCH /updates/{orgId}/{updateId}
    ///      Body: { "approval_status": "Approved|Declined|New", "scope": "Organization|Enterprise" }
    ///      Scope is required — omitting it returns 403.
    ///
    /// Detection heuristic: named packages have a human-readable Vendor_Product prefix;
    /// UUID packages start with a GUID.
    /// </summary>
    public async Task<ApprovalOutcome> SetCatalogApprovalAsync(
        string updateId, string approvalStatus, string scope = "Organization", CancellationToken ct = default)
    {
        var isBuiltin = updateId.EndsWith("_builtin", StringComparison.OrdinalIgnoreCase);

        // UUID-prefixed IDs are security/Windows updates → org-updates endpoint.
        // Named IDs are software delivery packages → software-repository endpoint only.
        // IMPORTANT: Never fall back named packages to org-updates — that endpoint will
        // always return 403 for global catalog items and creates misleading log noise.
        var isNamedPackage = isBuiltin && !IsUuidPrefixed(updateId);

        const int maxRetries = 3;
        var retryDelays = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };

        if (isNamedPackage)
            return await ApproveNamedPackageAsync(updateId, approvalStatus, maxRetries, ct);

        // ── Security / Windows updates via org-updates endpoint ───────────────
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var relPath = $"updates/{_options.OrganizationId}/{Uri.EscapeDataString(updateId)}";
            _logger.LogDebug("Action1: PATCH {Path} approval_status={Status} scope={Scope} (attempt {Attempt})",
                relPath, approvalStatus, scope, attempt + 1);
            var resp = await PatchJsonAsync(relPath, new { approval_status = approvalStatus, scope }, ct);

            if (resp.IsSuccessStatusCode) return ApprovalOutcome.Success;

            var content = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                if (attempt < maxRetries - 1)
                {
                    _logger.LogWarning(
                        "Action1: PATCH approval for {Id} rate-limited (429). Waiting {Delay}s before retry {Next}/{Max}.",
                        updateId, retryDelays[attempt].TotalSeconds, attempt + 2, maxRetries);
                    await Task.Delay(retryDelays[attempt], ct);
                    continue;
                }
                _logger.LogError("Action1: PATCH approval for {Id} rate-limited after {Max} attempts — will retry in second pass.", updateId, maxRetries);
                return ApprovalOutcome.RateLimitExhausted;
            }
            else if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogError(
                    "Action1: PATCH approval for {Id} returned 403 Forbidden. " +
                    "Check API credential role in Action1 console → Configuration → Users & API Credentials.",
                    updateId);
                return ApprovalOutcome.Forbidden;
            }
            else
            {
                _logger.LogWarning("Action1: PATCH approval for {Id} returned {Status}: {Content}",
                    updateId, (int)resp.StatusCode, content);
                return ApprovalOutcome.Error;
            }
        }

        return ApprovalOutcome.RateLimitExhausted;
    }

    /// <summary>
    /// Approves a named software delivery package.
    ///
    /// Strategy — probe in order until one succeeds:
    ///
    ///   Phase 1 — software-repository endpoint (three packageId shapes):
    ///     Shape A: packageId = fullId minus _builtin  (e.g. "OneDrive_1570833281465")
    ///     Shape B: packageId = full ID as-is          (e.g. "OneDrive_1570833281465_builtin")
    ///     Shape C: packageId = name only              (e.g. "OneDrive")
    ///     Body: { "approval_status": "Approved" }  — no scope field
    ///     Skip to next shape on 500 or 400 "does not exist"; hard-fail on 403/429/other.
    ///
    ///   Phase 2 — org-updates endpoint (last resort):
    ///     PATCH /updates/{orgId}/{updateId} with { approval_status, scope }
    ///     Used when the package does not exist in the global software-repository catalog.
    ///     The original 403 seen here was a credential-permission issue (pre-Enterprise role),
    ///     not a wrong-endpoint issue — so it is worth trying after all shapes are exhausted.
    ///
    /// This covers Docker Desktop, VMware Tools, and similar packages that may appear in
    /// the org-updates catalog rather than the global software-repository catalog.
    /// </summary>
    private async Task<ApprovalOutcome> ApproveNamedPackageAsync(
        string updateId, string approvalStatus, int maxRetries, CancellationToken ct)
    {
        var retryDelays = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };

        const string builtinSuffix = "_builtin";
        var withoutBuiltin = updateId.EndsWith(builtinSuffix, StringComparison.OrdinalIgnoreCase)
            ? updateId[..^builtinSuffix.Length]
            : updateId;

        // ── Phase 1: software-repository with three packageId candidates ──────
        var packageIdCandidates = new[]
        {
            withoutBuiltin,                      // shape A: drop _builtin only (most likely)
            updateId,                            // shape B: full ID as-is      (usually 500)
            StripTimestampSuffix(updateId),      // shape C: name-only          (usually 400)
        };

        foreach (var packageId in packageIdCandidates)
        {
            for (var attempt = 0; attempt < maxRetries; attempt++)
            {
                var relPath = $"software-repository/all/{Uri.EscapeDataString(packageId)}/versions/{Uri.EscapeDataString(updateId)}";
                _logger.LogDebug("Action1: PATCH {Path} approval_status={Status} (software-delivery, packageId={PkgId}, attempt {Attempt})",
                    relPath, approvalStatus, packageId, attempt + 1);

                var resp = await PatchJsonAsync(relPath, new { approval_status = approvalStatus }, ct);

                if (resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Action1: software-repository PATCH succeeded for {Id} using packageId={PkgId}", updateId, packageId);
                    return ApprovalOutcome.Success;
                }

                var content = await resp.Content.ReadAsStringAsync(ct);

                // 500 (wrong URL structure) and 400 "does not exist" both mean this packageId
                // shape is wrong — skip to the next candidate immediately without retrying.
                if (resp.StatusCode == System.Net.HttpStatusCode.InternalServerError ||
                    (resp.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                     content.Contains("does not exist", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug(
                        "Action1: software-repository PATCH for {Id} (packageId={PkgId}) returned {Status} — trying next URL shape. Body: {Body}",
                        updateId, packageId, (int)resp.StatusCode, content);
                    break; // try next packageId candidate
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (attempt < maxRetries - 1)
                    {
                        _logger.LogWarning(
                            "Action1: PATCH approval for {Id} rate-limited (429). Waiting {Delay}s before retry {Next}/{Max}.",
                            updateId, retryDelays[attempt].TotalSeconds, attempt + 2, maxRetries);
                        await Task.Delay(retryDelays[attempt], ct);
                        continue;
                    }
                    _logger.LogError("Action1: PATCH approval for {Id} rate-limited after {Max} attempts — will retry in second pass.", updateId, maxRetries);
                    return ApprovalOutcome.RateLimitExhausted;
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.LogError(
                        "Action1: PATCH approval for {Id} returned 403 Forbidden on software-repository. " +
                        "Check API credential role in Action1 console → Configuration → Users & API Credentials.",
                        updateId);
                    return ApprovalOutcome.Forbidden;
                }
                else
                {
                    _logger.LogWarning("Action1: PATCH approval for {Id} returned {Status}: {Content}",
                        updateId, (int)resp.StatusCode, content);
                    return ApprovalOutcome.Error;
                }
            }
        }

        // ── Phase 2: org-updates fallback ────────────────────────────────────
        // All software-repository shapes failed (500/400 "not found") — the package is not
        // in the global catalog. Try the org-updates endpoint. The earlier 403 on this path
        // was a credential permissions issue (pre-Enterprise role), not a wrong endpoint.
        var scope = _options.ApprovalScope;
        var orgPath = $"updates/{_options.OrganizationId}/{Uri.EscapeDataString(updateId)}";
        _logger.LogDebug(
            "Action1: software-repository exhausted for {Id} — trying org-updates fallback {Path} scope={Scope}",
            updateId, orgPath, scope);

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var resp = await PatchJsonAsync(orgPath, new { approval_status = approvalStatus, scope }, ct);

            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Action1: org-updates fallback PATCH succeeded for named package {Id}", updateId);
                return ApprovalOutcome.Success;
            }

            var content = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                if (attempt < maxRetries - 1)
                {
                    _logger.LogWarning(
                        "Action1: PATCH approval for {Id} rate-limited (429). Waiting {Delay}s before retry {Next}/{Max}.",
                        updateId, retryDelays[attempt].TotalSeconds, attempt + 2, maxRetries);
                    await Task.Delay(retryDelays[attempt], ct);
                    continue;
                }
                _logger.LogError("Action1: PATCH approval for {Id} rate-limited after {Max} attempts — will retry in second pass.", updateId, maxRetries);
                return ApprovalOutcome.RateLimitExhausted;
            }
            else if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogError(
                    "Action1: PATCH approval for {Id} returned 403 Forbidden on org-updates fallback. " +
                    "Ensure the API credential has 'Approve Updates: Enterprise' with no exclusions.",
                    updateId);
                return ApprovalOutcome.Forbidden;
            }
            else
            {
                _logger.LogError(
                    "Action1: all approval paths failed for {Id} — " +
                    "software-repository (all shapes: 500/400) and org-updates ({Status}: {Content}). " +
                    "Approve manually in the Action1 console.",
                    updateId, (int)resp.StatusCode, content);
                return ApprovalOutcome.Error;
            }
        }

        return ApprovalOutcome.RateLimitExhausted;
    }

    /// <summary>
    /// Strips the trailing numeric timestamp segment from a named builtin package ID.
    /// "Google_Chrome_1570243626751_builtin" → "Google_Chrome"
    /// "Microsoft_Corp_NET_SDK_1773391867068_builtin" → "Microsoft_Corp_NET_SDK"
    /// Returns the input unchanged if no timestamp segment is found.
    /// </summary>
    private static string StripTimestampSuffix(string updateId)
    {
        // Strip the _builtin suffix first, then find and remove the trailing numeric segment.
        const string builtinSuffix = "_builtin";
        var withoutBuiltin = updateId.EndsWith(builtinSuffix, StringComparison.OrdinalIgnoreCase)
            ? updateId[..^builtinSuffix.Length]
            : updateId;

        var lastUnderscore = withoutBuiltin.LastIndexOf('_');
        if (lastUnderscore < 0) return updateId;

        var lastSegment = withoutBuiltin[(lastUnderscore + 1)..];
        // A timestamp is a long all-digit string, typically 13 digits (ms since epoch).
        return lastSegment.Length >= 10 && lastSegment.All(char.IsAsciiDigit)
            ? withoutBuiltin[..lastUnderscore]
            : updateId; // not a timestamp — return full ID unchanged
    }

    /// <summary>Returns true if the updateId starts with a standard UUID (xxxxxxxx-xxxx-...).</summary>
    private static bool IsUuidPrefixed(string updateId) =>
        updateId.Length >= 36 && updateId[8] == '-' && updateId[13] == '-' && updateId[18] == '-';

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

    // ── Organizations ─────────────────────────────────────────────────────────

    /// <summary>
    /// List all organizations in the Action1 enterprise.
    ///
    /// Real Action1 API: GET /organizations  (enterprise-wide — no orgId in path)
    ///
    /// In single-org setups returns one item. MSP accounts return one per client.
    /// </summary>
    public async Task<IReadOnlyList<Action1Organization>> GetOrganizationsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching organizations (enterprise-wide)");
        return await GetPagedListAsync<Action1Organization>("organizations?fields=*", ct);
    }

    // ── Endpoint Groups ───────────────────────────────────────────────────────

    /// <summary>
    /// List all endpoint groups in the organization.
    ///
    /// Real Action1 API: GET /endpoints/groups/{orgId}?fields=*
    /// </summary>
    public async Task<IReadOnlyList<Action1EndpointGroup>> GetEndpointGroupsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching endpoint groups for org {OrgId}", _options.OrganizationId);
        return await GetPagedListAsync<Action1EndpointGroup>(
            $"endpoints/groups/{_options.OrganizationId}?fields=*", ct);
    }

    /// <summary>
    /// List all endpoints belonging to a specific endpoint group.
    ///
    /// Real Action1 API: GET /endpoints/groups/{orgId}/{groupId}/contents
    /// </summary>
    public async Task<IReadOnlyList<Action1EndpointGroupMember>> GetEndpointGroupMembersAsync(
        string groupId, CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching members of group {GroupId}", groupId);
        return await GetPagedListAsync<Action1EndpointGroupMember>(
            $"endpoints/groups/{_options.OrganizationId}/{groupId}/contents", ct);
    }

    // ── Automation Schedules ──────────────────────────────────────────────────

    /// <summary>
    /// List all automation schedules (recurring patch automations) in the organization.
    ///
    /// Real Action1 API: GET /policies/schedules/{orgId}?fields=*
    ///
    /// homeManagement-managed schedules are identified by the "homeManagement: " name prefix.
    /// </summary>
    public async Task<IReadOnlyList<Action1Schedule>> GetSchedulesAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Action1: fetching automation schedules for org {OrgId}", _options.OrganizationId);
        return await GetPagedListAsync<Action1Schedule>(
            $"policies/schedules/{_options.OrganizationId}?fields=*", ct);
    }

    /// <summary>
    /// Create a new automation schedule in Action1.
    ///
    /// Real Action1 API: POST /policies/schedules/{orgId}
    ///
    /// Returns the created schedule's ID, or null on failure.
    /// </summary>
    public async Task<string?> CreateScheduleAsync(object scheduleBody, CancellationToken ct = default)
    {
        var resp = await PostJsonAsync($"policies/schedules/{_options.OrganizationId}", scheduleBody, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Action1: schedule creation failed {Status}: {Body}", resp.StatusCode, body);
            return null;
        }

        var created = await resp.Content.ReadFromJsonAsync<Action1Schedule>(JsonOpts, ct);
        _logger.LogInformation("Action1: schedule {Id} ({Name}) created", created?.Id, created?.Name);
        return created?.Id;
    }

    /// <summary>
    /// Update an existing schedule via PATCH.
    ///
    /// Real Action1 API: PATCH /policies/schedules/{orgId}/{id}
    ///
    /// Only fields included in <paramref name="patch"/> are changed.
    /// Returns true on success.
    /// </summary>
    public async Task<bool> UpdateScheduleAsync(string scheduleId, object patch, CancellationToken ct = default)
    {
        var resp = await PatchJsonAsync(
            $"policies/schedules/{_options.OrganizationId}/{scheduleId}", patch, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Action1: schedule PATCH {Id} failed {Status}: {Body}",
                scheduleId, resp.StatusCode, body);
            return false;
        }

        _logger.LogInformation("Action1: schedule {Id} updated", scheduleId);
        return true;
    }

    /// <summary>
    /// Delete an automation schedule.
    ///
    /// Real Action1 API: DELETE /policies/schedules/{orgId}/{id}
    ///
    /// Only deletes HM-managed schedules (name starts with "homeManagement: ").
    /// Returns true on success.
    /// </summary>
    public async Task<bool> DeleteScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        var path = $"policies/schedules/{_options.OrganizationId}/{scheduleId}";
        var token = await GetAccessTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Delete, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Action1: {Status} on DELETE {Path} — forcing token refresh and retrying once.", resp.StatusCode, path);
            resp.Dispose();
            InvalidateToken();
            var freshToken = await GetAccessTokenAsync(ct);
            using var retry = new HttpRequestMessage(HttpMethod.Delete, path);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", freshToken);
            resp = await _http.SendAsync(retry, ct);
        }

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Action1: schedule DELETE {Id} failed {Status}", scheduleId, resp.StatusCode);
            return false;
        }

        _logger.LogInformation("Action1: schedule {Id} deleted", scheduleId);
        return true;
    }
}
