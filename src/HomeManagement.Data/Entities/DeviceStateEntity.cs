namespace HomeManagement.Data.Entities;

public sealed class DeviceStateEntity
{
    public Guid MachineId { get; set; }
    public MachineEntity Machine { get; set; } = null!; // Required EF navigation populated by EF.
    public DateTime? AgentAttemptUtc { get; set; }
    public DateTime? AgentSuccessUtc { get; set; }
    public DateTime? LastHeartbeatUtc { get; set; }
    public bool? LastKnownConnected { get; set; }
    public string? AgentFailure { get; set; }
    public string? ManagedObservationJson { get; set; }
    public DateTime? MetricsAttemptUtc { get; set; }
    public DateTime? MetricsSuccessUtc { get; set; }
    public string? HealthJson { get; set; }
    public string? MetricsFailure { get; set; }
}
