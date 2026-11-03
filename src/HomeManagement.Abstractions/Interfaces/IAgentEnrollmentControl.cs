using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

// Administrative metadata/lifecycle only. Provisioning secrets have no Broker interface.
public interface IAgentEnrollmentControl
{
    Task<AgentEnrollmentMetadata> GetEnrollmentAsync(Guid machineId, CancellationToken ct = default);
    Task<AgentEnrollmentMetadata> AssignEnrollmentAsync(Guid machineId, MachineAgentEnrollmentAssignment assignment, CancellationToken ct = default);
    Task<AgentEnrollmentMetadata> ChangeEnrollmentAsync(Guid machineId, AgentEnrollmentChange change, CancellationToken ct = default);
    Task<AgentEnrollmentMetadata> RebindEnrollmentAsync(Guid machineId, AgentEnrollmentRebind rebind, CancellationToken ct = default);
}
