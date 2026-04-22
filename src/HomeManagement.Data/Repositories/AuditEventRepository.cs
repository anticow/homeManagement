using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Repositories;

public sealed class AuditEventRepository : IAuditEventRepository
{
    private readonly HomeManagementDbContext _db;

    public AuditEventRepository(HomeManagementDbContext db) => _db = db;

    public async Task AddAsync(AuditEvent auditEvent, string? previousHash, string eventHash, int chainVersion, CancellationToken ct = default)
    {
        var entity = new AuditEventEntity
        {
            EventId = auditEvent.EventId,
            TimestampUtc = auditEvent.TimestampUtc,
            CorrelationId = auditEvent.CorrelationId,
            Action = auditEvent.Action,
            ActorIdentity = auditEvent.ActorIdentity,
            TargetMachineId = auditEvent.TargetMachineId,
            TargetMachineName = auditEvent.TargetMachineName,
            Detail = auditEvent.Detail,
            PropertiesJson = auditEvent.Properties is not null
                ? JsonSerializer.Serialize(auditEvent.Properties) : null,
            Outcome = auditEvent.Outcome,
            ErrorMessage = auditEvent.ErrorMessage,
            PreviousHash = previousHash,
            EventHash = eventHash,
            ChainVersion = chainVersion
        };

        await _db.AuditEvents.AddAsync(entity, ct);
    }

    public async Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        IQueryable<AuditEventEntity> q = _db.AuditEvents;

        if (query.FromUtc.HasValue)
            q = q.Where(e => e.TimestampUtc >= query.FromUtc.Value);
        if (query.ToUtc.HasValue)
            q = q.Where(e => e.TimestampUtc <= query.ToUtc.Value);
        if (query.Action.HasValue)
            q = q.Where(e => e.Action == query.Action.Value);
        if (query.Outcome.HasValue)
            q = q.Where(e => e.Outcome == query.Outcome.Value);
        if (query.MachineId.HasValue)
            q = q.Where(e => e.TargetMachineId == query.MachineId.Value);
        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
            q = q.Where(e => e.CorrelationId == query.CorrelationId);
        if (!string.IsNullOrWhiteSpace(query.ActorIdentity))
            q = q.Where(e => e.ActorIdentity == query.ActorIdentity);
        if (!string.IsNullOrWhiteSpace(query.DetailSearchText))
            q = q.Where(e => e.Detail != null && e.Detail.Contains(query.DetailSearchText));

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(e => e.TimestampUtc)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<AuditEvent>(items.Select(ToDomain).ToList(), total, query.Page, query.PageSize);
    }

    public async Task<long> CountAsync(AuditQuery query, CancellationToken ct = default)
    {
        IQueryable<AuditEventEntity> q = _db.AuditEvents;

        if (query.FromUtc.HasValue)
            q = q.Where(e => e.TimestampUtc >= query.FromUtc.Value);
        if (query.ToUtc.HasValue)
            q = q.Where(e => e.TimestampUtc <= query.ToUtc.Value);
        if (query.Action.HasValue)
            q = q.Where(e => e.Action == query.Action.Value);

        return await q.LongCountAsync(ct);
    }

    /// <summary>Returns the most recent HMAC chain (v1) event hash, or null if no v1 events exist yet.</summary>
    public async Task<string?> GetLastEventHashAsync(CancellationToken ct = default)
    {
        return await _db.AuditEvents
            .Where(e => e.ChainVersion == 1)
            .OrderByDescending(e => e.TimestampUtc)
            .Select(e => e.EventHash)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Iterates all chain-version-1 events in chronological order and verifies each HMAC.
    /// O(n) full-table scan — intended for periodic integrity checks, not hot path.
    /// </summary>
    public async Task<(bool Valid, long Verified, string? FailedAtEventId)> VerifyChainAsync(
        byte[] hmacKey, CancellationToken ct = default)
    {
        var events = await _db.AuditEvents
            .Where(e => e.ChainVersion == 1)
            .OrderBy(e => e.TimestampUtc)
            .ThenBy(e => e.EventId)
            .Select(e => new { e.EventId, e.TimestampUtc, e.Action, e.ActorIdentity, e.Outcome, e.PreviousHash, e.EventHash })
            .ToListAsync(ct);

        long verified = 0;
        string? previousHash = null;

        foreach (var e in events)
        {
            var payload = $"{previousHash ?? "GENESIS"}|{e.EventId}|{e.TimestampUtc:O}|{e.Action}|{e.ActorIdentity}|{e.Outcome}";
            using var hmac = new HMACSHA256(hmacKey);
            var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected),
                    Encoding.UTF8.GetBytes(e.EventHash ?? string.Empty)))
            {
                return (false, verified, e.EventId.ToString());
            }

            previousHash = e.EventHash;
            verified++;
        }

        return (true, verified, null);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }

    private static AuditEvent ToDomain(AuditEventEntity e)
    {
        var properties = !string.IsNullOrEmpty(e.PropertiesJson)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(e.PropertiesJson)
                as IReadOnlyDictionary<string, string>
            : null;

        return new AuditEvent(
            e.EventId, e.TimestampUtc, e.CorrelationId, e.Action, e.ActorIdentity,
            e.TargetMachineId, e.TargetMachineName, e.Detail, properties,
            e.Outcome, e.ErrorMessage);
    }
}

