namespace HomeManagement.Abstractions.Models;

// ── Audit ──

public record AuditEvent(
    Guid EventId,
    DateTime TimestampUtc,
    string CorrelationId,
    AuditAction Action,
    string ActorIdentity,
    Guid? TargetMachineId,
    string? TargetMachineName,
    string? Detail,
    IReadOnlyDictionary<string, string>? Properties,
    AuditOutcome Outcome,
    string? ErrorMessage);

public record AuditQuery(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    AuditAction? Action = null,
    AuditOutcome? Outcome = null,
    Guid? MachineId = null,
    string? CorrelationId = null,
    string? ActorIdentity = null,
    string? DetailSearchText = null,
    int Page = 1,
    int PageSize = 100);
