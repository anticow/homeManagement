namespace HomeManagement.Abstractions.Models;

// ── Agent ──

public record ConnectedAgent(
    string AgentId,
    string Hostname,
    string AgentVersion,
    DateTime ConnectedSinceUtc,
    DateTime LastHeartbeatUtc,
    TimeSpan Uptime,
    // Unlike the historical LastHeartbeatUtc field, this is null until a heartbeat is actually received.
    DateTime? LastReceivedHeartbeatUtc = null);

public record AgentMetadata(
    string AgentId,
    string Hostname,
    OsType OsType,
    string OsVersion,
    HardwareInfo Hardware);

public record AgentUpdatePackage(
    string Version,
    byte[] BinarySha256,
    string DownloadUrl);

public record AgentConnectionEvent(
    string AgentId,
    string Hostname,
    AgentConnectionEventType Type,
    DateTime TimestampUtc);
