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

    public Task UpdateAsync(Machine machine, CancellationToken ct = default)
    {
        var entity = ToEntity(machine);

        // Detach any previously tracked instance with the same key to avoid identity conflicts
        var tracked = _db.ChangeTracker.Entries<MachineEntity>()
            .FirstOrDefault(e => e.Entity.Id == entity.Id);
        if (tracked is not null)
            tracked.State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        _db.Machines.Update(entity);
        return Task.CompletedTask;
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
