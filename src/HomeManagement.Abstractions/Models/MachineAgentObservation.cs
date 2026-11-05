namespace HomeManagement.Abstractions.Models;

/// <summary>
/// Point-in-time bound session evidence, not command authorization.
/// LastHeartbeatUtc is the gateway's receipt time of an actual heartbeat, not connection initialization.
/// It is absent for disconnected sessions or older gateways; ObservedAtUtc is never a heartbeat.
/// </summary>
public sealed record MachineAgentObservation(
    Guid RegistryId, Guid MachineId, string AgentId, long BindingVersion, long CredentialGeneration,
    Guid? SessionId, bool Connected, DateTimeOffset ObservedAtUtc,
    DateTimeOffset? LastHeartbeatUtc = null);
