using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Transport;

public sealed partial class RemoteAgentGatewayClient : IAgentGateway, IAgentPeeringControl, IAgentEnrollmentControl, IMachineAgentRuntime, IFreshAgentSnapshotReader, IDisposable
{
    private const string HeaderName = "x-agent-gateway-api-key";

    private readonly AgentGatewayClientOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RemoteAgentGatewayClient> _logger;
    private readonly Subject<AgentConnectionEvent> _connectionSubject = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _snapshotLock = new();

    private List<ConnectedAgent> _snapshot = [];
    private Timer? _pollTimer;
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;
    private int _started;
    private bool _disposed;

    public RemoteAgentGatewayClient(IOptions<AgentGatewayClientOptions> options, ILogger<RemoteAgentGatewayClient> logger)
        : this(options, logger, new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    internal RemoteAgentGatewayClient(IOptions<AgentGatewayClientOptions> options,
        ILogger<RemoteAgentGatewayClient> logger, HttpMessageHandler handler)
    {
        var configured = options.Value;
        _options = new AgentGatewayClientOptions
        {
            ApiKey = configured.ApiKey,
            BaseUrl = configured.BaseUrl,
            PollIntervalSeconds = configured.PollIntervalSeconds,
            EnrollmentEnabled = configured.EnrollmentEnabled
        };
        _logger = logger;
        if (_options.EnrollmentEnabled && (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(address.UserInfo)))
            throw new InvalidOperationException("Managed enrollment requires an HTTPS Gateway without URL credentials.");
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_options.BaseUrl, UriKind.Absolute),
            // Per-request timeouts are applied in SendCommandAsync based on command.Timeout.
            // Set to infinite here to prevent HttpClient from cutting off long-running commands.
            Timeout = Timeout.InfiniteTimeSpan
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add(HeaderName, _options.ApiKey);
        }
    }

    public IObservable<AgentConnectionEvent> ConnectionEvents => _connectionSubject.AsObservable();

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            if (await VerifyEnrollmentModeAsync(ct)) return;
            await RefreshSnapshotAsync(ct);
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
        _pollTimer = new Timer(
            async _ => await SafePollAsync(),
            null,
            TimeSpan.FromSeconds(_options.PollIntervalSeconds),
            TimeSpan.FromSeconds(_options.PollIntervalSeconds));
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
        RequireLegacyRuntimeAsync(CancellationToken.None).GetAwaiter().GetResult();
        EnsureStarted();

        if (_snapshot.Count == 0 || DateTimeOffset.UtcNow - _lastRefreshUtc > TimeSpan.FromSeconds(_options.PollIntervalSeconds * 2))
        {
            try
            {
                RefreshSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to refresh connected agent snapshot from {BaseUrl}", _options.BaseUrl);
            }
        }

        lock (_snapshotLock)
        {
            return _snapshot;
        }
    }

    public async Task<IReadOnlyList<ConnectedAgent>> ReadFreshLegacySnapshotAsync(CancellationToken ct = default)
    {
        using var budget = EnrollmentBudget(ct);
        await RequireLegacyRuntimeAsync(budget.Token);
        using var response = await SendEnrollmentAsync(HttpMethod.Get, "/internal/agents", null, budget.Token);
        var agents = await ReadEnrollmentResponseAsync<List<ConnectedAgent>>(response, budget.Token, 4 * 1024 * 1024);
        if (agents.Any(a => a is null || string.IsNullOrWhiteSpace(a.AgentId) || string.IsNullOrWhiteSpace(a.Hostname)))
            throw new InvalidOperationException("Gateway returned an invalid fresh agent snapshot.");
        return agents;
    }

    public async Task<RemoteResult> SendCommandAsync(string agentId, RemoteCommand command, CancellationToken ct = default)
    {
        await RequireLegacyRuntimeAsync(ct);
        if (command.CommandType.Equals("ManagedServices", StringComparison.OrdinalIgnoreCase))
            RequireEncryptedControlPlane();
        EnsureStarted();
        // Apply per-request timeout: command timeout + 30s buffer for network and gateway overhead.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(command.Timeout + TimeSpan.FromSeconds(30));
        using var response = await _httpClient.PostAsJsonAsync(
            $"/internal/agents/{Uri.EscapeDataString(agentId)}/commands", command, timeoutCts.Token);
        return await ReadRequiredAsync<RemoteResult>(response, agentId, ct);
    }

    public async Task RevokeAsync(string agentId, CancellationToken ct = default)
    {
        RequireEncryptedControlPlane();
        using var budget = EnrollmentBudget(ct);
        var capabilities = await GetCapabilitiesAsync(budget.Token);
        if (_options.EnrollmentEnabled || capabilities.EnrollmentEnabled)
            throw new InvalidOperationException("Managed peering requires a machine binding and caller-confirmed version.");
        using var response = await _httpClient.PostAsJsonAsync(
            $"/internal/agents/{Uri.EscapeDataString(agentId)}/revoke",
            new { Reason = "Peering broken by HomeManagement operator" }, budget.Token);
        response.EnsureSuccessStatusCode();
    }

    private void RequireEncryptedControlPlane()
    {
        if (_httpClient.BaseAddress?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Managed service control and peering revocation require HTTPS to the Agent Gateway.");
    }

    public async Task<AgentMetadata> GetMetadataAsync(string agentId, CancellationToken ct = default)
    {
        await RequireLegacyRuntimeAsync(ct);
        EnsureStarted();
        using var response = await _httpClient.GetAsync($"/internal/agents/{Uri.EscapeDataString(agentId)}", ct);
        return await ReadRequiredAsync<AgentMetadata>(response, agentId, ct);
    }

    public async Task RequestUpdateAsync(string agentId, AgentUpdatePackage package, CancellationToken ct = default)
    {
        await RequireLegacyRuntimeAsync(ct);
        EnsureStarted();
        using var response = await _httpClient.PostAsJsonAsync($"/internal/agents/{Uri.EscapeDataString(agentId)}/updates", package, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Agent '{agentId}' is not connected.");
        }

        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer?.Dispose();
        _httpClient.Dispose();
        _refreshLock.Dispose();
        _connectionSubject.OnCompleted();
        _connectionSubject.Dispose();
    }

    private async Task SafePollAsync()
    {
        try
        {
            await RefreshSnapshotAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Agent gateway snapshot poll failed against {BaseUrl}", _options.BaseUrl);
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken ct)
    {
        await RequireLegacyRuntimeAsync(ct);
        await _refreshLock.WaitAsync(ct);
        try
        {
            using var response = await _httpClient.GetAsync("/internal/agents", ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("Agent gateway snapshot poll was rejected. Verify AgentGateway:ApiKey for {BaseUrl}", _options.BaseUrl);
                    return;
                }

                response.EnsureSuccessStatusCode();
            }

            var latest = await response.Content.ReadFromJsonAsync<List<ConnectedAgent>>(cancellationToken: ct) ?? [];
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
                _connectionSubject.OnNext(new AgentConnectionEvent(
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
                _connectionSubject.OnNext(new AgentConnectionEvent(
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

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, string agentId, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Agent '{agentId}' is not connected.");
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        return payload ?? throw new InvalidOperationException($"Agent gateway returned an empty payload for agent '{agentId}'.");
    }
}
