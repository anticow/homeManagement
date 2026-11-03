using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

public interface IMachineAgentRuntime
{
    Task<bool> VerifyEnrollmentModeAsync(CancellationToken ct = default);
    Task<RemoteResult> SendMachineCommandAsync(MachineTarget target, RemoteCommand command, CancellationToken ct = default);
    Task<AgentMetadata> GetMachineMetadataAsync(MachineTarget target, CancellationToken ct = default);
    Task RequestMachineUpdateAsync(MachineTarget target, AgentUpdatePackage package, CancellationToken ct = default);
    Task<MachineAgentObservation> GetMachineObservationAsync(Guid machineId, CancellationToken ct = default);
}
