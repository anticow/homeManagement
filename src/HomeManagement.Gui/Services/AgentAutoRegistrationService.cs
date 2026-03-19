using System.Reactive.Disposables;
using System.Reactive.Linq;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable CA1848 // Use LoggerMessage delegates
#pragma warning disable CA1873 // Logging argument evaluation

namespace HomeManagement.Gui.Services;

/// <summary>
/// Subscribes to <see cref="IAgentGateway.ConnectionEvents"/> and automatically
/// creates or updates <see cref="Machine"/> records in the inventory so that
/// agent-connected machines are visible in the GUI.
/// </summary>
internal sealed class AgentAutoRegistrationService : IDisposable
{
    private readonly IAgentGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentAutoRegistrationService> _logger;
    private readonly CompositeDisposable _disposables = [];

    public AgentAutoRegistrationService(
        IAgentGateway gateway,
        IServiceScopeFactory scopeFactory,
        ILogger<AgentAutoRegistrationService> logger)
    {
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Start()
    {
        _gateway.ConnectionEvents
            .ObserveOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .Subscribe(OnConnectionEvent)
            .DisposeWith(_disposables);

        // Register any agents that are already connected before we subscribed
        foreach (var agent in _gateway.GetConnectedAgents())
        {
            _ = RegisterAgentAsync(agent.AgentId, agent.Hostname);
        }
    }

    private void OnConnectionEvent(AgentConnectionEvent evt)
    {
        switch (evt.Type)
        {
            case AgentConnectionEventType.Connected:
                _ = RegisterAgentAsync(evt.AgentId, evt.Hostname);
                break;
            case AgentConnectionEventType.Disconnected:
                _ = MarkOfflineAsync(evt.Hostname);
                break;
        }
    }

    private async Task RegisterAgentAsync(string agentId, string hostname)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();

            // Check if machine already exists by hostname
            var existing = await inventory.QueryAsync(
                new MachineQuery(SearchText: hostname, PageSize: 100));

            var match = existing.Items.FirstOrDefault(m =>
                m.Hostname.Value.Equals(hostname, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                // Update existing machine to Online + Agent protocol
                await inventory.UpdateAsync(match.Id, new MachineUpdateRequest(
                    ConnectionMode: MachineConnectionMode.Agent,
                    Protocol: TransportProtocol.Agent,
                    State: MachineState.Online));

                _logger.LogInformation("Agent {AgentId} reconnected — updated machine {MachineId} ({Hostname})",
                    agentId, match.Id, hostname);
            }
            else
            {
                // Determine OS from agent metadata if available
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
            _logger.LogError(ex, "Failed to mark machine offline for agent hostname {Hostname}", hostname);
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
