using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Repositories;

public sealed class PatchHistoryRepository : IPatchHistoryRepository
{
    private readonly HomeManagementDbContext _db;

    public PatchHistoryRepository(HomeManagementDbContext db) => _db = db;

    public async Task<IReadOnlyList<PatchHistoryEntry>> GetByMachineAsync(Guid machineId, CancellationToken ct = default)
    {
        var entities = await _db.PatchHistory
            .Where(p => p.MachineId == machineId)
            .OrderByDescending(p => p.TimestampUtc)
            .ToListAsync(ct);

        return entities.Select(ToDomain).ToList();
    }

    public async Task AddAsync(PatchHistoryEntry entry, CancellationToken ct = default)
    {
        var entity = ToEntity(entry);
        await _db.PatchHistory.AddAsync(entity, ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }

    private static PatchHistoryEntry ToDomain(PatchHistoryEntity e) => new(
        e.Id, e.MachineId, e.PatchId, e.Title, e.State, e.TimestampUtc, e.ErrorMessage);

    private static PatchHistoryEntity ToEntity(PatchHistoryEntry e) => new()
    {
        Id = e.Id,
        MachineId = e.MachineId,
        PatchId = e.PatchId,
        Title = e.Title,
        State = e.State,
        TimestampUtc = e.TimestampUtc,
        ErrorMessage = e.ErrorMessage
    };
}
