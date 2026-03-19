using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Auditing;

/// <summary>
/// Structured, tamper-evident audit logging. Each event is linked to the previous
/// via an HMAC-SHA256 hash chain, guaranteeing that deletions or modifications are detectable.
/// Sensitive data is automatically redacted before persistence.
/// </summary>
internal sealed class AuditLoggerService : IAuditLogger
{
    private readonly IAuditEventRepository _repository;
    private readonly ISensitiveDataFilter _filter;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<AuditLoggerService> _logger;
    private readonly object _chainLock = new();

    public AuditLoggerService(
        IAuditEventRepository repository,
        ISensitiveDataFilter filter,
        ICorrelationContext correlation,
        ILogger<AuditLoggerService> logger)
    {
        _repository = repository;
        _filter = filter;
        _correlation = correlation;
        _logger = logger;
    }

    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        // Redact sensitive data from detail and properties
        var redacted = auditEvent with
        {
            Detail = _filter.Redact(auditEvent.Detail),
            Properties = _filter.RedactProperties(auditEvent.Properties),
            CorrelationId = string.IsNullOrEmpty(auditEvent.CorrelationId)
                ? _correlation.CorrelationId
                : auditEvent.CorrelationId
        };

        // Compute HMAC chain hash
        string? previousHash;
        string eventHash;
        lock (_chainLock)
        {
            previousHash = _repository.GetLastEventHashAsync(ct).GetAwaiter().GetResult();
            eventHash = ComputeEventHash(redacted, previousHash);
        }

        await _repository.AddAsync(redacted, previousHash, eventHash, ct);
        await _repository.SaveChangesAsync(ct);

        _logger.LogInformation("[{CorrelationId}] Audit: {Action} by {Actor} — {Outcome}",
            redacted.CorrelationId, redacted.Action, redacted.ActorIdentity, redacted.Outcome);
    }

    public async Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        return await _repository.QueryAsync(query, ct);
    }

    public async Task<long> CountAsync(AuditQuery query, CancellationToken ct = default)
    {
        return await _repository.CountAsync(query, ct);
    }

    public async Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default)
    {
        var result = await _repository.QueryAsync(query with { PageSize = 50000 }, ct);

        if (format == ExportFormat.Json)
        {
            await JsonSerializer.SerializeAsync(destination, result.Items, cancellationToken: ct);
        }
        else
        {
            await using var writer = new StreamWriter(destination, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync("EventId,TimestampUtc,CorrelationId,Action,Actor,MachineId,Outcome,Detail");
            foreach (var e in result.Items)
            {
                var detail = e.Detail?.Replace("\"", "\"\"", StringComparison.Ordinal) ?? "";
                await writer.WriteLineAsync(
                    $"{e.EventId},{e.TimestampUtc:O},{e.CorrelationId},{e.Action},{e.ActorIdentity},{e.TargetMachineId},{e.Outcome},\"{detail}\"");
            }
        }
    }

    /// <summary>
    /// Compute HMAC-SHA256 hash for chain integrity.
    /// Hash = HMAC(previousHash + eventId + timestamp + action + actor + outcome).
    /// </summary>
    private static string ComputeEventHash(AuditEvent evt, string? previousHash)
    {
        var payload = $"{previousHash ?? "GENESIS"}|{evt.EventId}|{evt.TimestampUtc:O}|{evt.Action}|{evt.ActorIdentity}|{evt.Outcome}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
