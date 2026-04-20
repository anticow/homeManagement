using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Auditing;

/// <summary>
/// Structured, tamper-evident audit logging. Each event is linked to the previous
/// via an HMAC-SHA256 hash chain (chain version 1), guaranteeing that deletions or
/// modifications are detectable. Legacy events with chain version 0 used plain SHA-256
/// and are preserved but not part of the HMAC chain.
/// Sensitive data is automatically redacted before persistence.
/// </summary>
internal sealed class AuditLoggerService : IAuditLogger
{
    private const int ChainVersion = 1;

    private readonly IAuditEventRepository _repository;
    private readonly ISensitiveDataFilter _filter;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<AuditLoggerService> _logger;
    private readonly byte[] _hmacKey;
    private readonly object _chainLock = new();

    public AuditLoggerService(
        IAuditEventRepository repository,
        ISensitiveDataFilter filter,
        ICorrelationContext correlation,
        ILogger<AuditLoggerService> logger,
        IOptions<AuditOptions> options)
    {
        _repository = repository;
        _filter = filter;
        _correlation = correlation;
        _logger = logger;
        _hmacKey = options.Value.GetKeyBytes();
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

        // Compute HMAC-SHA256 chain hash — lock ensures no two events race for the same previousHash
        string? previousHash;
        string eventHash;
        lock (_chainLock)
        {
            previousHash = _repository.GetLastEventHashAsync(ct).GetAwaiter().GetResult();
            eventHash = ComputeEventHash(redacted, previousHash, _hmacKey);
        }

        await _repository.AddAsync(redacted, previousHash, eventHash, ChainVersion, ct);
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
                await writer.WriteLineAsync(string.Join(',',
                    CsvField(e.EventId.ToString()),
                    CsvField(e.TimestampUtc.ToString("O")),
                    CsvField(e.CorrelationId),
                    CsvField(e.Action.ToString()),
                    CsvField(e.ActorIdentity),
                    CsvField(e.TargetMachineId?.ToString()),
                    CsvField(e.Outcome.ToString()),
                    CsvField(e.Detail)));
            }
        }
    }

    /// <summary>
    /// Compute HMAC-SHA256 chain hash.
    /// Hash = HMAC_SHA256(key, previousHash + "|" + eventId + "|" + timestamp + "|" + action + "|" + actor + "|" + outcome)
    /// Using HMAC prevents an attacker with read-only database access from recomputing valid hashes.
    /// </summary>
    internal static string ComputeEventHash(AuditEvent evt, string? previousHash, byte[] hmacKey)
    {
        var payload = $"{previousHash ?? "GENESIS"}|{evt.EventId}|{evt.TimestampUtc:O}|{evt.Action}|{evt.ActorIdentity}|{evt.Outcome}";
        using var hmac = new HMACSHA256(hmacKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Quotes a CSV field and defends against spreadsheet formula injection (CWE-1236).
    /// All fields are double-quoted; values starting with formula trigger characters
    /// are prefixed with a tab to prevent execution in spreadsheet applications.
    /// </summary>
    private static string CsvField(string? value)
    {
        var s = value ?? string.Empty;
        // Prefix formula-injection trigger characters per OWASP CSV injection guidance
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            s = '\t' + s;
        return $"\"{s.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
