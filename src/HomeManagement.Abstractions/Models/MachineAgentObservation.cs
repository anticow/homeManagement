namespace HomeManagement.Abstractions.Models;

public sealed record MachineAgentObservation(
    Guid RegistryId, Guid MachineId, string AgentId, long BindingVersion, long CredentialGeneration,
    Guid? SessionId, bool Connected, DateTimeOffset ObservedAtUtc);
