using System.Net;
using System.Net.Http.Headers;
using Refit;

namespace HomeManagement.Web.Services;

/// <summary>
/// Session-aware Broker API client that forwards the current server-side access token.
/// </summary>
public sealed class BrokerApiClient : IBrokerApi
{
    public const string HttpClientName = "BrokerApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ServerSessionState _sessionState;
    private readonly IWebSessionAuthService _authService;

    public BrokerApiClient(
        IHttpClientFactory httpClientFactory,
        ServerSessionState sessionState,
        IWebSessionAuthService authService)
    {
        _httpClientFactory = httpClientFactory;
        _sessionState = sessionState;
        _authService = authService;
    }

    public Task<HomeManagement.Abstractions.Models.PagedResult<HomeManagement.Abstractions.Models.Machine>> GetMachinesAsync(int page = 1, int pageSize = 25, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetMachinesAsync(page, pageSize, ct), ct);

    public Task<HomeManagement.Abstractions.Models.Machine> GetMachineAsync(Guid id, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetMachineAsync(id, ct), ct);

    public Task<HomeManagement.Abstractions.Models.Machine> CreateMachineAsync([Body] HomeManagement.Abstractions.Models.MachineCreateRequest request, CancellationToken ct = default)
        => ExecuteAsync(api => api.CreateMachineAsync(request, ct), ct);

    public Task DeleteMachineAsync(Guid id, CancellationToken ct = default)
        => ExecuteAsync(api => api.DeleteMachineAsync(id, ct), ct);

    public Task<IReadOnlyList<HomeManagement.Abstractions.Models.PatchInfo>> ScanPatchesAsync([Body] PatchScanRequest request, CancellationToken ct = default)
        => ExecuteAsync(api => api.ScanPatchesAsync(request, ct), ct);

    public Task<IReadOnlyList<HomeManagement.Abstractions.Models.PatchHistoryEntry>> GetPatchHistoryAsync(Guid machineId, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetPatchHistoryAsync(machineId, ct), ct);

    public Task<IReadOnlyList<HomeManagement.Abstractions.Models.ServiceInfo>> GetServicesAsync(Guid machineId, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetServicesAsync(machineId, ct), ct);

    public Task<HomeManagement.Abstractions.Models.PagedResult<HomeManagement.Abstractions.Models.JobSummary>> GetJobsAsync(int page = 1, int pageSize = 25, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetJobsAsync(page, pageSize, ct), ct);

    public Task<HomeManagement.Abstractions.Models.JobStatus> GetJobAsync(Guid id, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetJobAsync(id, ct), ct);

    public Task<IReadOnlyList<HomeManagement.Abstractions.Models.CredentialEntry>> GetCredentialsAsync(CancellationToken ct = default)
        => ExecuteAsync(api => api.GetCredentialsAsync(ct), ct);

    public Task<HomeManagement.Abstractions.Models.PagedResult<HomeManagement.Abstractions.Models.AuditEvent>> GetAuditEventsAsync(int page = 1, int pageSize = 50, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetAuditEventsAsync(page, pageSize, ct), ct);

    private async Task<T> ExecuteAsync<T>(Func<IBrokerApi, Task<T>> action, CancellationToken ct)
    {
        try
        {
            var api = await CreateApiAsync(ct);
            return await action(api);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (!await _authService.RefreshAsync(ct))
            {
                throw CreateUnauthorizedException(ex);
            }

            try
            {
                var api = await CreateApiAsync(ct, refreshIfNeeded: false);
                return await action(api);
            }
            catch (ApiException retryEx) when (retryEx.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionState.Clear();
                throw CreateUnauthorizedException(retryEx);
            }
        }
    }

    private async Task ExecuteAsync(Func<IBrokerApi, Task> action, CancellationToken ct)
    {
        try
        {
            var api = await CreateApiAsync(ct);
            await action(api);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (!await _authService.RefreshAsync(ct))
            {
                throw CreateUnauthorizedException(ex);
            }

            try
            {
                var api = await CreateApiAsync(ct, refreshIfNeeded: false);
                await action(api);
            }
            catch (ApiException retryEx) when (retryEx.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionState.Clear();
                throw CreateUnauthorizedException(retryEx);
            }
        }
    }

    private async Task<IBrokerApi> CreateApiAsync(CancellationToken ct, bool refreshIfNeeded = true)
    {
        if (refreshIfNeeded && NeedsRefresh())
        {
            var refreshed = await _authService.RefreshAsync(ct);
            if (!refreshed)
            {
                _sessionState.Clear();
                throw new UnauthorizedAccessException("Web session has expired.");
            }
        }

        if (string.IsNullOrWhiteSpace(_sessionState.AccessToken))
        {
            throw new UnauthorizedAccessException("Web session is not authenticated.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _sessionState.AccessToken);
        return RestService.For<IBrokerApi>(client);
    }

    private bool NeedsRefresh()
    {
        return _sessionState.AccessTokenExpiresUtc is { } expiresUtc
            && expiresUtc <= DateTimeOffset.UtcNow.AddMinutes(1);
    }

    private UnauthorizedAccessException CreateUnauthorizedException(Exception innerException)
    {
        _sessionState.Clear();
        return new UnauthorizedAccessException("Web session has expired.", innerException);
    }
}