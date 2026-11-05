namespace HomeManagement.Abstractions.Models;

/// <summary>Durable evidence only. Never used to authorize commands or update historical inventory state.</summary>
public sealed record DeviceStateSnapshot
{
    public required Guid MachineId { get; init; }
    public DateTimeOffset? AgentAttemptUtc { get; init; }
    public DateTimeOffset? AgentSuccessUtc { get; init; }
    public DateTimeOffset? LastHeartbeatUtc { get; init; }
    public bool? LastKnownConnected { get; init; }
    public string? AgentFailure { get; init; }
    public MachineAgentObservation? ManagedObservation { get; init; }
    public DateTimeOffset? MetricsAttemptUtc { get; init; }
    public DateTimeOffset? MetricsSuccessUtc { get; init; }
    public AgentHealthSummary? Health { get; init; }
    public string? MetricsFailure { get; init; }
}

public enum DeviceConnectivity { Unknown, Online, Offline, NotApplicable }

public sealed record FleetDeviceState(
    Machine Machine, DeviceStateSnapshot Snapshot, DeviceConnectivity Connectivity,
    double? AgentAgeSeconds, bool AgentIsStale, double? MetricsAgeSeconds, bool MetricsIsStale);

public sealed class DeviceStateOptions
{
    public const string SectionName = "DeviceState";
    public int ValidationIntervalSeconds { get; set; } = 300;
    public int StaleAfterSeconds { get; set; } = 1500;
}
