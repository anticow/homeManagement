using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Provides structured, tamper-evident logging of every action taken through the system.
/// </summary>
public interface IAuditLogger
{
    Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default);
    Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default);
    Task<long> CountAsync(AuditQuery query, CancellationToken ct = default);
    Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default);
}
