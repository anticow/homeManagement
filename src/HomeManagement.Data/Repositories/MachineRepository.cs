using System.Net;
using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Repositories;

public sealed class MachineRepository : IMachineRepository
{
    private readonly HomeManagementDbContext _db;

    public MachineRepository(HomeManagementDbContext db) => _db = db;

    public async Task<Machine?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.Machines
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default)
    {
        IQueryable<MachineEntity> q = _db.Machines.Include(m => m.Tags);

        if (query.IncludeDeleted)
            q = q.IgnoreQueryFilters();

        if (query.OsType.HasValue)
            q = q.Where(m => m.OsType == query.OsType.Value);

        if (query.State.HasValue)
            q = q.Where(m => m.State == query.State.Value);

        if (query.ConnectionMode.HasValue)
            q = q.Where(m => m.ConnectionMode == query.ConnectionMode.Value);

        if (!string.IsNullOrWhiteSpace(query.Tag))
            q = q.Where(m => m.Tags.Any(t => t.Key == query.Tag));

        if (!string.IsNullOrWhiteSpace(query.SearchText))
            q = q.Where(m => m.Hostname.Contains(query.SearchText) || (m.Fqdn != null && m.Fqdn.Contains(query.SearchText)));

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderBy(m => m.Hostname)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<Machine>(items.Select(ToDomain).ToList(), total, query.Page, query.PageSize);
    }

    public async Task AddAsync(Machine machine, CancellationToken ct = default)
    {
        var entity = ToEntity(machine);
        await _db.Machines.AddAsync(entity, ct);
    }

    public async Task AddRangeAsync(IReadOnlyList<Machine> machines, CancellationToken ct = default)
    {
        var entities = machines.Select(ToEntity).ToList();
        await _db.Machines.AddRangeAsync(entities, ct);
    }

    public async Task UpdateAsync(Machine machine, CancellationToken ct = default)
    {
        // Load the tracked entity with its tags so we can patch in-place.
        // Re-building via ToEntity() generates new GUIDs for MachineTagEntity,
        // which violates the unique (MachineId, Key) index on every Update call.
        var entity = await _db.Machines
            .Include(m => m.Tags)
            .FirstAsync(m => m.Id == machine.Id, ct);

        entity.Hostname = machine.Hostname.ToString();
        entity.Fqdn = machine.Fqdn;
        entity.IpAddressesJson = JsonSerializer.Serialize(machine.IpAddresses.Select(ip => ip.ToString()));
        entity.OsType = machine.OsType;
        entity.OsVersion = machine.OsVersion;
        entity.ConnectionMode = machine.ConnectionMode;
        entity.Protocol = machine.Protocol;
        entity.Port = machine.Port;
        entity.CredentialId = machine.CredentialId;
        entity.State = machine.State;
        entity.CpuCores = machine.Hardware?.CpuCores;
        entity.RamBytes = machine.Hardware?.RamBytes;
        entity.Architecture = machine.Hardware?.Architecture;
        entity.DisksJson = machine.Hardware is not null ? JsonSerializer.Serialize(machine.Hardware.Disks) : null;
        entity.CreatedUtc = machine.CreatedUtc;
        entity.UpdatedUtc = machine.UpdatedUtc;
        entity.LastContactUtc = machine.LastContactUtc;
        entity.AgentVersion = machine.AgentVersion;
        entity.IsDeleted = machine.IsDeleted;

        // Sync tags by key — preserves existing row GUIDs to avoid unique-index violations.
        var existingByKey = entity.Tags.ToDictionary(t => t.Key, t => t);
        var desiredKeys = machine.Tags.Keys.ToHashSet();

        foreach (var tag in entity.Tags.Where(t => !desiredKeys.Contains(t.Key)).ToList())
            entity.Tags.Remove(tag);

        foreach (var (key, value) in machine.Tags)
        {
            if (existingByKey.TryGetValue(key, out var existing))
                existing.Value = value;
            else
                entity.Tags.Add(new MachineTagEntity { Id = Guid.NewGuid(), MachineId = entity.Id, Key = key, Value = value });
        }
    }

    public async Task SoftDeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.Machines.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Machine {id} not found.");
        entity.IsDeleted = true;
        entity.UpdatedUtc = DateTime.UtcNow;
    }

    public async Task SoftDeleteRangeAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entities = await _db.Machines
            .Where(m => ids.Contains(m.Id))
            .ToListAsync(ct);

        foreach (var entity in entities)
        {
            entity.IsDeleted = true;
            entity.UpdatedUtc = now;
        }
    }

    private static Machine ToDomain(MachineEntity e)
    {
        var ipAddresses = string.IsNullOrEmpty(e.IpAddressesJson) ? []
            : JsonSerializer.Deserialize<string[]>(e.IpAddressesJson)
                ?.Select(IPAddress.Parse).ToArray() ?? [];

        var disks = string.IsNullOrEmpty(e.DisksJson) ? []
            : JsonSerializer.Deserialize<DiskInfo[]>(e.DisksJson) ?? [];

        var hardware = e.CpuCores.HasValue
            ? new HardwareInfo(e.CpuCores.Value, e.RamBytes ?? 0, disks, e.Architecture ?? "unknown")
            : null;

        var tags = e.Tags.ToDictionary(t => t.Key, t => t.Value).AsReadOnly();

        return new Machine(
            e.Id,
            Hostname.Create(e.Hostname),
            e.Fqdn,
            ipAddresses,
            e.OsType,
            e.OsVersion,
            e.ConnectionMode,
            e.Protocol,
            e.Port,
            e.CredentialId,
            e.State,
            tags,
            hardware,
            e.CreatedUtc,
            e.UpdatedUtc,
            e.LastContactUtc,
            e.AgentVersion,
            e.IsDeleted);
    }

    private static MachineEntity ToEntity(Machine m) => new()
    {
        Id = m.Id,
        Hostname = m.Hostname.ToString(),
        Fqdn = m.Fqdn,
        IpAddressesJson = JsonSerializer.Serialize(m.IpAddresses.Select(ip => ip.ToString())),
        OsType = m.OsType,
        OsVersion = m.OsVersion,
        ConnectionMode = m.ConnectionMode,
        Protocol = m.Protocol,
        Port = m.Port,
        CredentialId = m.CredentialId,
        State = m.State,
        CpuCores = m.Hardware?.CpuCores,
        RamBytes = m.Hardware?.RamBytes,
        Architecture = m.Hardware?.Architecture,
        DisksJson = m.Hardware is not null ? JsonSerializer.Serialize(m.Hardware.Disks) : null,
        CreatedUtc = m.CreatedUtc,
        UpdatedUtc = m.UpdatedUtc,
        LastContactUtc = m.LastContactUtc,
        AgentVersion = m.AgentVersion,
        IsDeleted = m.IsDeleted,
        Tags = m.Tags.Select(t => new MachineTagEntity
        {
            Id = Guid.NewGuid(),
            MachineId = m.Id,
            Key = t.Key,
            Value = t.Value
        }).ToList()
    };
}
