using System.Reactive.Linq;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Transport;

/// <summary>
/// Subscribes to <see cref="IAgentGateway.ConnectionEvents"/> and automatically
/// creates or updates <see cref="Machine"/> records in inventory when agents
/// connect or disconnect. Works in any host (Broker, GUI, etc.).
/// </summary>
internal sealed class AgentAutoRegistrationHostedService : BackgroundService
{
    private readonly IAgentGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentAutoRegistrationHostedService> _logger;
    private readonly IMachineAgentRuntime? _runtime;

    public AgentAutoRegistrationHostedService(
        IAgentGateway gateway,
        IServiceScopeFactory scopeFactory,
        ILogger<AgentAutoRegistrationHostedService> logger, IMachineAgentRuntime? runtime = null)
    {
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _runtime = runtime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtime is not null && await _runtime.VerifyEnrollmentModeAsync(stoppingToken))
        {
            // Managed observations are deliberately not persisted into inventory.
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                if (!await _runtime.VerifyEnrollmentModeAsync(stoppingToken))
                    throw new InvalidOperationException("Gateway authority changed; managed inventory synchronization remains disabled.");
            }
            return;
        }
        // Wait for the agent gateway to become available (handles startup ordering)
        const int maxRetries = 30;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await _gateway.StartAsync(stoppingToken);
                break;
            }
            catch (HttpRequestException ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (attempt == maxRetries)
                    throw;

                _logger.LogWarning(ex,
                    "Agent gateway not reachable (attempt {Attempt}/{Max}), retrying in {Delay}s",
                    attempt, maxRetries, attempt * 2);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), stoppingToken);
            }
        }

        // Wait briefly for the first poll to complete
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        // Register any agents that are already connected
        foreach (var agent in _gateway.GetConnectedAgents())
        {
            await RegisterAgentAsync(agent.AgentId, agent.Hostname);
        }

        // Subscribe to connection events for the lifetime of the service
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = stoppingToken.Register(() => tcs.TrySetResult());

        using var subscription = _gateway.ConnectionEvents
            .ObserveOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .Subscribe(evt =>
            {
                _ = evt.Type switch
                {
                    AgentConnectionEventType.Connected => RegisterAgentAsync(evt.AgentId, evt.Hostname),
                    AgentConnectionEventType.Disconnected => MarkOfflineAsync(evt.Hostname),
                    _ => Task.CompletedTask
                };
            });

        if (_runtime is null) await tcs.Task;
        else
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                if (await _runtime.VerifyEnrollmentModeAsync(stoppingToken))
                    throw new InvalidOperationException("Gateway mode changed; legacy inventory synchronization cannot continue.");
            }
        }
    }

    private async Task RegisterAgentAsync(string agentId, string hostname)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            if (_runtime is not null && await _runtime.VerifyEnrollmentModeAsync())
                throw new InvalidOperationException("Legacy inventory synchronization cannot use managed enrollment.");
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();

            var existing = await inventory.QueryAsync(
                new MachineQuery(SearchText: hostname, PageSize: 100));

            var match = existing.Items.FirstOrDefault(m =>
                m.Hostname.Value.Equals(hostname, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                var agentVersion = _gateway.GetConnectedAgents()
                    .FirstOrDefault(a => a.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase))
                    ?.AgentVersion;

                await inventory.UpdateAsync(match.Id, new MachineUpdateRequest(
                    ConnectionMode: MachineConnectionMode.Agent,
                    Protocol: TransportProtocol.Agent,
                    State: MachineState.Online,
                    LastContactUtc: DateTime.UtcNow,
                    AgentVersion: agentVersion));

                _logger.LogInformation("Agent {AgentId} reconnected — updated machine {MachineId} ({Hostname})",
                    agentId, match.Id, hostname);
            }
            else
            {
                var osType = OsType.Windows;
                try
                {
                    var metadata = await _gateway.GetMetadataAsync(agentId);
                    osType = metadata.OsType;
                }
#pragma warning disable CA1031
                catch { /* Use default */ }
#pragma warning restore CA1031

                if (!Hostname.TryCreate(hostname, out var validHostname, out _))
                {
                    _logger.LogWarning("Agent {AgentId} has invalid hostname '{Hostname}' — skipping registration",
                        agentId, hostname);
                    return;
                }

                var machine = await inventory.AddAsync(new MachineCreateRequest(
                    Hostname: validHostname,
                    Fqdn: null,
                    OsType: osType,
                    ConnectionMode: MachineConnectionMode.Agent,
                    Protocol: TransportProtocol.Agent,
                    Port: 0,
                    CredentialId: Guid.Empty,
                    Tags: new Dictionary<string, string>
                    {
                        ["agentId"] = agentId,
                        ["autoRegistered"] = "true"
                    }));

                _logger.LogInformation("Agent {AgentId} auto-registered as machine {MachineId} ({Hostname})",
                    agentId, machine.Id, hostname);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Failed to auto-register agent {AgentId} ({Hostname})", agentId, hostname);
        }
    }

    private async Task MarkOfflineAsync(string hostname)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            if (_runtime is not null && await _runtime.VerifyEnrollmentModeAsync())
                throw new InvalidOperationException("Legacy inventory synchronization cannot use managed enrollment.");
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();

            var existing = await inventory.QueryAsync(
                new MachineQuery(SearchText: hostname, PageSize: 100));

            var match = existing.Items.FirstOrDefault(m =>
                m.Hostname.Value.Equals(hostname, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                await inventory.UpdateAsync(match.Id, new MachineUpdateRequest(
                    State: MachineState.Offline));

                _logger.LogInformation("Agent disconnected — machine {MachineId} ({Hostname}) marked offline",
                    match.Id, hostname);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Failed to mark agent offline ({Hostname})", hostname);
        }
    }
}
