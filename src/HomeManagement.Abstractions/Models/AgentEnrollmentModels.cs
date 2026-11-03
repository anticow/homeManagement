using System.Text.Json.Serialization;

namespace HomeManagement.Abstractions.Models;

public enum AgentEnrollmentState { Pending, Enabled, Revoked }
public enum AgentEnrollmentTransition { Rotate, Revoke, Reenable }

public sealed record AgentEnrollmentMetadata(
    string AgentId, string InventoryHostname, string ExpectedWireHostname, Guid? MachineId,
    AgentEnrollmentState State, long Version, long CredentialGeneration, DateTimeOffset UpdatedUtc);

public sealed record AgentEnrollmentAssignment(
    string AgentId, string InventoryHostname, string ExpectedWireHostname, Guid? MachineId = null,
    bool Revoked = false);

public sealed record AgentEnrollmentChange(
    [property: JsonRequired] long ExpectedVersion, [property: JsonRequired] AgentEnrollmentTransition Transition);
public sealed record AgentEnrollmentRebind(
    [property: JsonRequired] long ExpectedVersion, string InventoryHostname, string ExpectedWireHostname);
public sealed record MachineAgentEnrollmentAssignment(string AgentId, string InventoryHostname, string ExpectedWireHostname);
public sealed record AgentEnrollmentCapabilities([property: JsonRequired] bool EnrollmentEnabled);
