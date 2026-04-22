using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Auth;
using HomeManagement.Transport;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Gui.Services;

public sealed class DesktopPlatformOptions
{
    public string BrokerBaseUrl { get; init; } = string.Empty;
    public string AuthBaseUrl { get; init; } = string.Empty;
    public string? Username { get; init; }

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(BrokerBaseUrl)
        && !string.IsNullOrWhiteSpace(AuthBaseUrl);

    public static DesktopPlatformOptions FromConfiguration(IConfiguration configuration)
    {
        return new DesktopPlatformOptions
        {
            BrokerBaseUrl = configuration["BrokerApi:BaseUrl"] ?? string.Empty,
            AuthBaseUrl = configuration["AuthApi:BaseUrl"] ?? string.Empty,
            Username = configuration["DesktopAuth:Username"]
        };
    }
}

public sealed class DesktopSessionState
{
    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(AccessToken);
    public ClaimsPrincipal User { get; private set; } = new(new ClaimsIdentity());
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public DateTimeOffset? AccessTokenExpiresUtc { get; private set; }

    public event Action? SessionChanged;

    public void SetSession(string accessToken, string refreshToken)
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        User = new ClaimsPrincipal(new ClaimsIdentity(token.Claims, "desktop-session"));
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        AccessTokenExpiresUtc = token.ValidTo == DateTime.MinValue
            ? null
            : new DateTimeOffset(token.ValidTo, TimeSpan.Zero);

        SessionChanged?.Invoke();
    }

    public void Clear()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity());
        AccessToken = null;
        RefreshToken = null;
        AccessTokenExpiresUtc = null;
        SessionChanged?.Invoke();
    }
}

internal sealed class DesktopAuthService : IDisposable
{
    private readonly DesktopPlatformOptions _options;
    private readonly DesktopSessionState _sessionState;
    private readonly HttpClient _authClient;

    public DesktopAuthService(DesktopPlatformOptions options, DesktopSessionState sessionState)
    {
        _options = options;
        _sessionState = sessionState;
        _authClient = new HttpClient
        {
            BaseAddress = new Uri(_options.AuthBaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task UnlockAsync(SecureString password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Username))
        {
            throw new InvalidOperationException("DesktopAuth:Username must be configured for platform mode.");
        }

        var plainTextPassword = new NetworkCredential(string.Empty, password).Password;
        var response = await _authClient.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(_options.Username, plainTextPassword, AuthProviderType.Local),
            ct);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AuthResult>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Auth service returned an empty login response.");

        if (!result.Success || result.AccessToken is null || result.RefreshToken is null)
        {
            throw new UnauthorizedAccessException(result.Error ?? "Desktop platform sign-in failed.");
        }

        _sessionState.SetSession(result.AccessToken, result.RefreshToken);
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_sessionState.RefreshToken))
        {
            _sessionState.Clear();
            return false;
        }

        var response = await _authClient.PostAsJsonAsync(
            "/api/auth/refresh",
            new DesktopRefreshRequest(_sessionState.RefreshToken),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            _sessionState.Clear();
            return false;
        }

        var result = await response.Content.ReadFromJsonAsync<AuthResult>(cancellationToken: ct);
        if (result is null || !result.Success || result.AccessToken is null || result.RefreshToken is null)
        {
            _sessionState.Clear();
            return false;
        }

        _sessionState.SetSession(result.AccessToken, result.RefreshToken);
        return true;
    }

    public async Task LockAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_sessionState.RefreshToken))
        {
            try
            {
                await _authClient.PostAsJsonAsync(
                    "/api/auth/revoke",
                    new DesktopRevokeRequest(_sessionState.RefreshToken),
                    ct);
            }
            catch (HttpRequestException)
            {
            }
        }

        _sessionState.Clear();
    }

    public async Task EnsureAuthenticatedAsync(CancellationToken ct = default)
    {
        if (!_sessionState.IsAuthenticated)
        {
            throw new UnauthorizedAccessException("Desktop platform session is not authenticated.");
        }

        if (_sessionState.AccessTokenExpiresUtc is { } expiresUtc
            && expiresUtc <= DateTimeOffset.UtcNow.AddMinutes(1)
            && !await RefreshAsync(ct))
        {
            throw new UnauthorizedAccessException("Desktop platform session has expired.");
        }
    }

    public void Dispose()
    {
        _authClient.Dispose();
    }
}

internal sealed class DesktopBrokerClient : IDisposable
{
    private readonly DesktopAuthService _authService;
    private readonly DesktopSessionState _sessionState;
    private readonly HttpClient _brokerClient;
    private readonly JsonSerializerOptions _serializerOptions;

    public DesktopBrokerClient(
        DesktopPlatformOptions options,
        DesktopAuthService authService,
        DesktopSessionState sessionState)
    {
        _authService = authService;
        _sessionState = sessionState;
        _brokerClient = new HttpClient
        {
            BaseAddress = new Uri(options.BrokerBaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };

        _serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _serializerOptions.Converters.Add(new HostnameJsonConverter());
        _serializerOptions.Converters.Add(new ServiceNameJsonConverter());
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredAsync<T>(response, ct);
    }

    public async Task<T?> GetOptionalAsync<T>(string path, CancellationToken ct = default) where T : class
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await ReadRequiredAsync<T>(response, ct);
    }

    public async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, ct);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredAsync<T>(response, ct);
    }

    public async Task PostAsync(string path, object? body, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]> PostForBytesAsync(string path, object? body, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        bool allowRefresh = true)
    {
        await _authService.EnsureAuthenticatedAsync(ct);

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sessionState.AccessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _serializerOptions);
        }

        var response = await _brokerClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && allowRefresh)
        {
            response.Dispose();
            if (await _authService.RefreshAsync(ct))
            {
                return await SendAsync(method, path, body, ct, allowRefresh: false);
            }

            throw new UnauthorizedAccessException("Desktop platform session has expired.");
        }

        return response;
    }

    private async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await response.Content.ReadFromJsonAsync<T>(_serializerOptions, ct);
        return payload ?? throw new InvalidOperationException("Broker returned an empty response payload.");
    }

    public void Dispose()
    {
        _brokerClient.Dispose();
    }
}

internal sealed class DesktopInventoryService : IInventoryService
{
    private readonly DesktopBrokerClient _brokerClient;

    public DesktopInventoryService(DesktopBrokerClient brokerClient)
    {
        _brokerClient = brokerClient;
    }

    public Task<Machine> AddAsync(MachineCreateRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Machine creation is not yet supported by the desktop platform client.");

    public Task<Machine> UpdateAsync(Guid id, MachineUpdateRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Machine updates are not yet supported by the desktop platform client.");

    public Task RemoveAsync(Guid id, CancellationToken ct = default) =>
        _brokerClient.DeleteAsync($"/api/machines/{id}", ct);

    public Task BatchRemoveAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        throw new NotSupportedException("Batch remove is not yet supported by the desktop platform client.");

    public async Task<Machine?> GetAsync(Guid id, CancellationToken ct = default) =>
        await _brokerClient.GetOptionalAsync<Machine>($"/api/machines/{id}", ct);

    public Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default)
    {
        var path = DesktopPlatformQueryString.Add("/api/machines", new Dictionary<string, string?>
        {
            ["searchText"] = query.SearchText,
            ["osType"] = query.OsType?.ToString(),
            ["state"] = query.State?.ToString(),
            ["page"] = query.Page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = query.PageSize.ToString(CultureInfo.InvariantCulture)
        });

        return _brokerClient.GetAsync<PagedResult<Machine>>(path, ct);
    }

    public Task<Machine> RefreshMetadataAsync(Guid id, CancellationToken ct = default) =>
        throw new NotSupportedException("Metadata refresh is not yet supported by the desktop platform client.");

    public Task<IReadOnlyList<Machine>> DiscoverAsync(CidrRange range, CancellationToken ct = default) =>
        throw new NotSupportedException("Discovery is not yet supported by the desktop platform client.");

    public Task ImportAsync(Stream csvStream, CancellationToken ct = default) =>
        throw new NotSupportedException("Import is not yet supported by the desktop platform client.");

    public Task ExportAsync(MachineQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) =>
        throw new NotSupportedException("Export is not yet supported by the desktop platform client.");
}

internal sealed class DesktopPatchService : IPatchService
{
    private readonly DesktopBrokerClient _brokerClient;

    public DesktopPatchService(DesktopBrokerClient brokerClient)
    {
        _brokerClient = brokerClient;
    }

    public Task<IReadOnlyList<PatchInfo>> DetectAsync(MachineTarget target, CancellationToken ct = default) =>
        _brokerClient.PostAsync<IReadOnlyList<PatchInfo>>("/api/patching/scan", new DesktopPatchScanRequest(target.MachineId), ct);

    public async IAsyncEnumerable<PatchInfo> DetectStreamAsync(MachineTarget target, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var patches = await DetectAsync(target, ct);
        foreach (var patch in patches)
        {
            yield return patch;
        }
    }

    public Task<PatchResult> ApplyAsync(MachineTarget target, IReadOnlyList<PatchInfo> patches, PatchOptions options, CancellationToken ct = default) =>
        throw new NotSupportedException("Patch apply is not yet supported by the desktop platform client.");

    public Task<PatchResult> VerifyAsync(MachineTarget target, IReadOnlyList<string> patchIds, CancellationToken ct = default) =>
        throw new NotSupportedException("Patch verification is not yet supported by the desktop platform client.");

    public Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(Guid machineId, CancellationToken ct = default) =>
        _brokerClient.GetAsync<IReadOnlyList<PatchHistoryEntry>>($"/api/patching/{machineId}/history", ct);

    public Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(MachineTarget target, CancellationToken ct = default) =>
        _brokerClient.GetAsync<IReadOnlyList<InstalledPatch>>($"/api/patching/{target.MachineId}/installed", ct);
}

internal sealed class DesktopServiceControllerClient : IServiceController
{
    private readonly DesktopBrokerClient _brokerClient;

    public DesktopServiceControllerClient(DesktopBrokerClient brokerClient)
    {
        _brokerClient = brokerClient;
    }

    public async Task<ServiceInfo> GetStatusAsync(MachineTarget target, ServiceName serviceName, CancellationToken ct = default)
    {
        var services = await ListServicesAsync(target, new ServiceFilter(NamePattern: serviceName.Value), ct);
        return services.FirstOrDefault(svc => svc.Name == serviceName)
            ?? throw new KeyNotFoundException($"Service '{serviceName}' was not found on machine '{target.Hostname}'.");
    }

    public Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default) =>
        _brokerClient.GetAsync<IReadOnlyList<ServiceInfo>>($"/api/services/{target.MachineId}", ct);

    public async IAsyncEnumerable<ServiceInfo> ListServicesStreamAsync(MachineTarget target, ServiceFilter? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var services = await ListServicesAsync(target, filter, ct);
        foreach (var service in services)
        {
            yield return service;
        }
    }

    public Task<ServiceActionResult> ControlAsync(MachineTarget target, ServiceName serviceName, ServiceAction action, CancellationToken ct = default) =>
        _brokerClient.PostAsync<ServiceActionResult>(
            $"/api/services/{target.MachineId}/control",
            new DesktopServiceControlRequest(serviceName.Value, action),
            ct);

    public async Task<IReadOnlyList<ServiceActionResult>> BulkControlAsync(IReadOnlyList<MachineTarget> targets, ServiceName serviceName, ServiceAction action, CancellationToken ct = default)
    {
        var results = new List<ServiceActionResult>(targets.Count);
        foreach (var target in targets)
        {
            results.Add(await ControlAsync(target, serviceName, action, ct));
        }

        return results;
    }
}

internal sealed class DesktopRemoteExecutor : IRemoteExecutor
{
    private readonly DesktopBrokerClient _brokerClient;

    public DesktopRemoteExecutor(DesktopBrokerClient brokerClient)
    {
        _brokerClient = brokerClient;
    }

    public Task<RemoteResult> ExecuteAsync(MachineTarget target, RemoteCommand command, CancellationToken ct = default) =>
        throw new NotSupportedException("Direct remote command execution is not supported in desktop platform mode.");

    public Task TransferFileAsync(MachineTarget target, FileTransferRequest request, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException("File transfer is not supported in desktop platform mode.");

    public Task<ConnectionTestResult> TestConnectionAsync(MachineTarget target, CancellationToken ct = default) =>
        _brokerClient.PostAsync<ConnectionTestResult>($"/api/machines/{target.MachineId}/test", body: null, ct);
}

internal sealed class DesktopBrokerAgentGatewayClient : IAgentGateway, IDisposable
{
    private readonly DesktopBrokerClient _brokerClient;
    private readonly Subject<AgentConnectionEvent> _connectionEvents = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _snapshotLock = new();

    private List<ConnectedAgent> _snapshot = [];
    private Timer? _pollTimer;
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;
    private int _started;
    private bool _disposed;

    public DesktopBrokerAgentGatewayClient(DesktopBrokerClient brokerClient)
    {
        _brokerClient = brokerClient;
    }

    public IObservable<AgentConnectionEvent> ConnectionEvents => _connectionEvents.AsObservable();

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        await RefreshSnapshotAsync(ct);
        _pollTimer = new Timer(
            async _ => await SafePollAsync(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        Interlocked.Exchange(ref _started, 0);
        return Task.CompletedTask;
    }

    public IReadOnlyList<ConnectedAgent> GetConnectedAgents()
    {
        EnsureStarted();

        if (_snapshot.Count == 0 || DateTimeOffset.UtcNow - _lastRefreshUtc > TimeSpan.FromSeconds(10))
        {
            try
            {
                RefreshSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        lock (_snapshotLock)
        {
            return _snapshot;
        }
    }

    public Task<RemoteResult> SendCommandAsync(string agentId, RemoteCommand command, CancellationToken ct = default) =>
        _brokerClient.PostAsync<RemoteResult>($"/api/agents/{Uri.EscapeDataString(agentId)}/commands", command, ct);

    public Task<AgentMetadata> GetMetadataAsync(string agentId, CancellationToken ct = default) =>
        _brokerClient.GetAsync<AgentMetadata>($"/api/agents/{Uri.EscapeDataString(agentId)}", ct);

    public Task RequestUpdateAsync(string agentId, AgentUpdatePackage package, CancellationToken ct = default) =>
        _brokerClient.PostAsync($"/api/agents/{Uri.EscapeDataString(agentId)}/updates", package, ct);

    private async Task SafePollAsync()
    {
        try
        {
            await RefreshSnapshotAsync(CancellationToken.None);
        }
        catch
        {
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var latest = await _brokerClient.GetAsync<List<ConnectedAgent>>("/api/agents", ct);
            PublishSnapshotDiff(latest);
            lock (_snapshotLock)
            {
                _snapshot = latest;
            }

            _lastRefreshUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void PublishSnapshotDiff(IReadOnlyList<ConnectedAgent> latest)
    {
        IReadOnlyList<ConnectedAgent> previous;
        lock (_snapshotLock)
        {
            previous = _snapshot;
        }

        var previousIndex = previous.ToDictionary(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase);
        var latestIndex = latest.ToDictionary(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase);

        foreach (var agent in latest)
        {
            if (!previousIndex.ContainsKey(agent.AgentId))
            {
                _connectionEvents.OnNext(new AgentConnectionEvent(
                    agent.AgentId,
                    agent.Hostname,
                    AgentConnectionEventType.Connected,
                    DateTime.UtcNow));
            }
        }

        foreach (var agent in previous)
        {
            if (!latestIndex.ContainsKey(agent.AgentId))
            {
                _connectionEvents.OnNext(new AgentConnectionEvent(
                    agent.AgentId,
                    agent.Hostname,
                    AgentConnectionEventType.Disconnected,
                    DateTime.UtcNow));
            }
        }
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _started) == 0)
        {
            StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer?.Dispose();
        _refreshLock.Dispose();
        _connectionEvents.OnCompleted();
        _connectionEvents.Dispose();
    }
}

internal sealed class DesktopJobSchedulerClient : IJobScheduler
{
    private readonly DesktopBrokerClient _brokerClient;
    private readonly IObservable<JobProgressEvent> _progressStream = Observable.Interval(TimeSpan.FromSeconds(5))
        .Select(_ => new JobProgressEvent(new JobId(Guid.Empty), Guid.Empty, string.Empty, string.Empty, 0));

    public DesktopJobSchedulerClient(DesktopBrokerClient brokerClient)
    {
        _brokerClient = brokerClient;
    }

    public Task<JobId> SubmitAsync(JobDefinition job, CancellationToken ct = default) =>
        SubmitInternalAsync(job, ct);

    public Task<ScheduleId> ScheduleAsync(JobDefinition job, string cronExpression, CancellationToken ct = default) =>
        throw new NotSupportedException("Scheduling is not yet supported by the desktop platform client.");

    public Task CancelAsync(JobId jobId, CancellationToken ct = default) =>
        _brokerClient.DeleteAsync($"/api/jobs/{jobId.Value}", ct);

    public Task UnscheduleAsync(ScheduleId scheduleId, CancellationToken ct = default) =>
        throw new NotSupportedException("Scheduling is not yet supported by the desktop platform client.");

    public Task<JobStatus> GetStatusAsync(JobId jobId, CancellationToken ct = default) =>
        _brokerClient.GetAsync<JobStatus>($"/api/jobs/{jobId.Value}", ct);

    public Task<PagedResult<JobSummary>> ListJobsAsync(JobQuery query, CancellationToken ct = default)
    {
        var path = DesktopPlatformQueryString.Add("/api/jobs", new Dictionary<string, string?>
        {
            ["page"] = query.Page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = query.PageSize.ToString(CultureInfo.InvariantCulture)
        });

        return _brokerClient.GetAsync<PagedResult<JobSummary>>(path, ct);
    }

    public Task<IReadOnlyList<ScheduledJobSummary>> ListSchedulesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Scheduling is not yet supported by the desktop platform client.");

    public IObservable<JobProgressEvent> ProgressStream => _progressStream;

    public IObservable<JobProgressEvent> GetJobProgressStream(JobId jobId) => _progressStream;

    private async Task<JobId> SubmitInternalAsync(JobDefinition job, CancellationToken ct)
    {
        var response = await _brokerClient.PostAsync<DesktopJobSubmissionResponse>("/api/jobs", job, ct);
        return new JobId(response.JobId);
    }
}

internal sealed class DesktopAuditLoggerClient : IAuditLogger
{
    private readonly DesktopBrokerClient _brokerClient;

    public DesktopAuditLoggerClient(DesktopBrokerClient brokerClient)
    {
        _brokerClient = brokerClient;
    }

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default) =>
        throw new NotSupportedException("Audit recording is owned by the Broker service in platform mode.");

    public Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        var path = DesktopPlatformQueryString.Add("/api/audit", new Dictionary<string, string?>
        {
            ["action"] = query.Action?.ToString(),
            ["fromUtc"] = query.FromUtc?.ToString("O", CultureInfo.InvariantCulture),
            ["toUtc"] = query.ToUtc?.ToString("O", CultureInfo.InvariantCulture),
            ["page"] = query.Page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = query.PageSize.ToString(CultureInfo.InvariantCulture)
        });

        return _brokerClient.GetAsync<PagedResult<AuditEvent>>(path, ct);
    }

    public async Task<long> CountAsync(AuditQuery query, CancellationToken ct = default)
    {
        var page = await QueryAsync(query with { Page = 1, PageSize = 1 }, ct);
        return page.TotalCount;
    }

    public async Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default)
    {
        var payload = await _brokerClient.PostForBytesAsync("/api/audit/export", query, ct);
        await destination.WriteAsync(payload, ct);
        destination.Position = 0;
    }
}

internal sealed class DesktopPlatformCredentialVault : ICredentialVault, IDisposable
{
    private readonly DesktopAuthService _authService;
    private readonly DesktopBrokerClient _brokerClient;
    private readonly DesktopSessionState _sessionState;
    private readonly BehaviorSubject<bool> _lockState;

    public DesktopPlatformCredentialVault(
        DesktopAuthService authService,
        DesktopBrokerClient brokerClient,
        DesktopSessionState sessionState)
    {
        _authService = authService;
        _brokerClient = brokerClient;
        _sessionState = sessionState;
        _lockState = new BehaviorSubject<bool>(_sessionState.IsAuthenticated);
        _sessionState.SessionChanged += OnSessionChanged;
    }

    public Task UnlockAsync(SecureString masterPassword, CancellationToken ct = default) =>
        _authService.UnlockAsync(masterPassword, ct);

    public Task LockAsync(CancellationToken ct = default) =>
        _authService.LockAsync(ct);

    public bool IsUnlocked => _sessionState.IsAuthenticated;

    public IObservable<bool> LockStateChanged => _lockState.AsObservable();

    public Task<CredentialEntry> AddAsync(CredentialCreateRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Credential creation is not yet supported by the desktop platform client.");

    public Task<CredentialEntry> UpdateAsync(Guid id, CredentialUpdateRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("Credential updates are not yet supported by the desktop platform client.");

    public Task RemoveAsync(Guid id, CancellationToken ct = default) =>
        _brokerClient.DeleteAsync($"/api/credentials/{id}", ct);

    public Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken ct = default) =>
        _brokerClient.GetAsync<IReadOnlyList<CredentialEntry>>("/api/credentials", ct);

    public Task<CredentialPayload> GetPayloadAsync(Guid id, CancellationToken ct = default) =>
        throw new NotSupportedException("Credential secret retrieval is not supported by the desktop platform client.");

    public Task RotateEncryptionKeyAsync(SecureString newMasterPassword, CancellationToken ct = default) =>
        throw new NotSupportedException("Vault key rotation is not supported by the desktop platform client.");

    public Task<byte[]> ExportAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Vault export is not supported by the desktop platform client.");

    public Task ImportAsync(byte[] encryptedBlob, SecureString password, CancellationToken ct = default) =>
        throw new NotSupportedException("Vault import is not supported by the desktop platform client.");

    private void OnSessionChanged()
    {
        _lockState.OnNext(_sessionState.IsAuthenticated);
    }

    public void Dispose()
    {
        _sessionState.SessionChanged -= OnSessionChanged;
        _lockState.Dispose();
    }
}

internal sealed class DesktopPlatformHealthService : ISystemHealthService
{
    private readonly DesktopPlatformOptions _options;

    public DesktopPlatformHealthService(DesktopPlatformOptions options)
    {
        _options = options;
    }

    public async Task<SystemHealthReport> CheckAsync(CancellationToken ct = default)
    {
        var components = new List<ComponentHealth>
        {
            await CheckComponentAsync("Broker API", _options.BrokerBaseUrl, ct),
            await CheckComponentAsync("Auth API", _options.AuthBaseUrl, ct)
        };

        var overall = components.Any(component => component.Status == HealthStatus.Unhealthy)
            ? HealthStatus.Unhealthy
            : components.Any(component => component.Status == HealthStatus.Degraded)
                ? HealthStatus.Degraded
                : HealthStatus.Healthy;

        return new SystemHealthReport(overall, components, DateTime.UtcNow);
    }

    private static async Task<ComponentHealth> CheckComponentAsync(string name, string baseUrl, CancellationToken ct)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(5)
        };

        var started = DateTime.UtcNow;
        try
        {
            using var response = await client.GetAsync("/healthz", ct);
            var latency = DateTime.UtcNow - started;
            return response.IsSuccessStatusCode
                ? new ComponentHealth(name, HealthStatus.Healthy, null, latency)
                : new ComponentHealth(name, HealthStatus.Degraded, $"HTTP {(int)response.StatusCode}", latency);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ComponentHealth(name, HealthStatus.Unhealthy, ex.Message, null);
        }
    }
}

internal sealed class HostnameJsonConverter : JsonConverter<Hostname>
{
    public override Hostname Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return Hostname.Create(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected hostname object payload.");
        }

        string? value = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var propertyName = reader.GetString();
            reader.Read();
            if (string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase))
            {
                value = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return Hostname.Create(value ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, Hostname value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("value", value.Value);
        writer.WriteEndObject();
    }
}

internal sealed class ServiceNameJsonConverter : JsonConverter<ServiceName>
{
    public override ServiceName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return ServiceName.Create(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected service-name object payload.");
        }

        string? value = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            var propertyName = reader.GetString();
            reader.Read();
            if (string.Equals(propertyName, "value", StringComparison.OrdinalIgnoreCase))
            {
                value = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return ServiceName.Create(value ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, ServiceName value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("value", value.Value);
        writer.WriteEndObject();
    }
}

public static class DesktopPlatformServiceCollectionExtensions
{
    public static IServiceCollection AddDesktopPlatformClients(
        this IServiceCollection services,
        DesktopPlatformOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<DesktopSessionState>();
        services.AddSingleton<DesktopAuthService>();
        services.AddSingleton<DesktopBrokerClient>();
        services.AddSingleton<IInventoryService, DesktopInventoryService>();
        services.AddSingleton<IPatchService, DesktopPatchService>();
        services.AddSingleton<IServiceController, DesktopServiceControllerClient>();
        services.AddSingleton<IJobScheduler, DesktopJobSchedulerClient>();
        services.AddSingleton<IAuditLogger, DesktopAuditLoggerClient>();
        services.AddSingleton<ICredentialVault, DesktopPlatformCredentialVault>();
        services.AddSingleton<ISystemHealthService, DesktopPlatformHealthService>();
        services.AddSingleton<IRemoteExecutor, DesktopRemoteExecutor>();
        services.AddSingleton<IAgentGateway, DesktopBrokerAgentGatewayClient>();
        return services;
    }
}

internal sealed record DesktopPatchScanRequest(Guid MachineId);
internal sealed record DesktopServiceControlRequest(string ServiceName, ServiceAction Action);
internal sealed record DesktopJobSubmissionResponse(Guid JobId);
internal sealed record DesktopRefreshRequest(string RefreshToken);
internal sealed record DesktopRevokeRequest(string RefreshToken);

internal static class DesktopPlatformQueryString
{
    public static string Add(string path, IDictionary<string, string?> queryParameters)
    {
        var filtered = queryParameters
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        return filtered.Count == 0 ? path : QueryHelpers.AddQueryString(path, filtered);
    }
}
