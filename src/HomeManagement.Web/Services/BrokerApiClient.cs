using System.Net;
using System.Net.Http.Headers;
using Refit;

namespace HomeManagement.Web.Services;

/// <summary>
/// Session-aware Broker API client that forwards the current server-side access token.
/// </summary>
public sealed class BrokerApiClient : IBrokerApi, IDisposable
{
    public const string HttpClientName = "BrokerApi";

    // Serialises proactive token refreshes within a single circuit so that concurrent
    // API calls don't all race to refresh simultaneously when the token is near expiry.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

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

    public Task<HomeManagement.Abstractions.Models.MachineStateSnapshot> GetMachineStateAsync(Guid id, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetMachineStateAsync(id, ct), ct);

    public Task<HomeManagement.Abstractions.Models.MachineSummary> GetMachineSummaryAsync(CancellationToken ct = default)
        => ExecuteAsync(api => api.GetMachineSummaryAsync(ct), ct);

    public Task<IReadOnlyList<HomeManagement.Abstractions.Models.ProcessInfo>> GetMachineProcessesAsync(Guid id, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetMachineProcessesAsync(id, ct), ct);

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

    public Task<IReadOnlyList<Action1PatchDto>> GetAction1PatchesAsync(string endpointId, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetAction1PatchesAsync(endpointId, ct), ct);

    public Task<Action1DeploymentCreatedDto> DeployAction1PatchesAsync(string endpointId, [Body] Action1DeployRequestDto request, CancellationToken ct = default)
        => ExecuteAsync(api => api.DeployAction1PatchesAsync(endpointId, request, ct), ct);

    public Task<Action1DeploymentStatusDto> GetAction1DeploymentAsync(string deploymentId, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetAction1DeploymentAsync(deploymentId, ct), ct);

    public Task<HomeManagement.Abstractions.Models.PagedResult<HomeManagement.Abstractions.Models.JobSummary>> GetJobsAsync(int page = 1, int pageSize = 25, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetJobsAsync(page, pageSize, ct), ct);

    public Task<HomeManagement.Abstractions.Models.JobStatus> GetJobAsync(Guid id, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetJobAsync(id, ct), ct);

    public Task<IReadOnlyList<HomeManagement.Abstractions.Models.CredentialEntry>> GetCredentialsAsync(CancellationToken ct = default)
        => ExecuteAsync(api => api.GetCredentialsAsync(ct), ct);

    public Task<HomeManagement.Abstractions.Models.PagedResult<HomeManagement.Abstractions.Models.AuditEvent>> GetAuditEventsAsync(int page = 1, int pageSize = 50, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetAuditEventsAsync(page, pageSize, ct), ct);

    public Task<IReadOnlyList<FleetMachineStatusDto>> GetFleetStatusAsync(CancellationToken ct = default)
        => ExecuteAsync(api => api.GetFleetStatusAsync(ct), ct);

    public Task<FleetPatchSummaryDto> GetFleetSummaryAsync(CancellationToken ct = default)
        => ExecuteAsync(api => api.GetFleetSummaryAsync(ct), ct);

    public Task<IReadOnlyList<Action1PatchDto>> GetMachinePatchesAsync(Guid machineId, CancellationToken ct = default)
        => ExecuteAsync(api => api.GetMachinePatchesAsync(machineId, ct), ct);

    public Task<ApproveDeploymentResultDto> ApprovePatchesAsync(Guid machineId, [Body] Action1DeployRequestDto request, CancellationToken ct = default)
        => ExecuteAsync(api => api.ApprovePatchesAsync(machineId, request, ct), ct);

    public Task<IReadOnlyList<Action1VulnerabilityDto>> GetFleetVulnerabilitiesAsync(CancellationToken ct = default)
        => ExecuteAsync(api => api.GetFleetVulnerabilitiesAsync(ct), ct);

    public Task<IReadOnlyList<ScheduleDto>> GetSchedulesAsync(CancellationToken ct = default)
        => ExecuteAsync(api => api.GetSchedulesAsync(ct), ct);

    public Task<ScheduleSyncResultDto> SyncSchedulesAsync(CancellationToken ct = default)
        => ExecuteAsync(api => api.SyncSchedulesAsync(ct), ct);

    public Task PatchScheduleAsync(string scheduleId, SchedulePatchRequestDto request, CancellationToken ct = default)
        => ExecuteAsync(api => api.PatchScheduleAsync(scheduleId, request, ct), ct);

    public Task DeleteScheduleAsync(string scheduleId, CancellationToken ct = default)
        => ExecuteAsync(api => api.DeleteScheduleAsync(scheduleId, ct), ct);

    private async Task<T> ExecuteAsync<T>(Func<IBrokerApi, Task<T>> action, CancellationToken ct)
    {
        try
        {
            var api = await CreateApiAsync(ct);
            return await action(api);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("Service is temporarily busy. Please try again in a moment.", ex);
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
        catch (ApiException ex)
        {
            // Extract the ProblemDetails 'detail' field so the UI shows the actual error
            // rather than the generic "Response status code does not indicate success: 502" message.
            var detail = TryExtractProblemDetail(ex.Content);
            throw new InvalidOperationException(detail ?? ex.Message, ex);
        }
    }

    private async Task ExecuteAsync(Func<IBrokerApi, Task> action, CancellationToken ct)
    {
        try
        {
            var api = await CreateApiAsync(ct);
            await action(api);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("Service is temporarily busy. Please try again in a moment.", ex);
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
        catch (ApiException ex)
        {
            var detail = TryExtractProblemDetail(ex.Content);
            throw new InvalidOperationException(detail ?? ex.Message, ex);
        }
    }

    private static string? TryExtractProblemDetail(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == System.Text.Json.JsonValueKind.String)
                return detail.GetString();
            if (root.TryGetProperty("title", out var title) && title.ValueKind == System.Text.Json.JsonValueKind.String)
                return title.GetString();
        }
        catch { /* non-JSON body; fall through */ }
        return null;
    }

    private async Task<IBrokerApi> CreateApiAsync(CancellationToken ct, bool refreshIfNeeded = true)
    {
        if (refreshIfNeeded && NeedsRefresh())
        {
            // Serialise within-circuit concurrent refreshes: the first caller refreshes;
            // subsequent callers see the updated token and skip the refresh entirely.
            await _refreshLock.WaitAsync(ct);
            try
            {
                if (NeedsRefresh()) // re-check after acquiring the lock
                {
                    var refreshed = await _authService.RefreshAsync(ct);
                    if (!refreshed)
                    {
                        _sessionState.Clear();
                        throw new UnauthorizedAccessException("Web session has expired.");
                    }
                }
            }
            finally
            {
                _refreshLock.Release();
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

    private static UnauthorizedAccessException CreateUnauthorizedException(Exception innerException)
        => new("Web session has expired.", innerException);

    public void Dispose() => _refreshLock.Dispose();
}
