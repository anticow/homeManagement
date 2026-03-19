using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Repositories;

public sealed class JobRepository : IJobRepository
{
    private readonly HomeManagementDbContext _db;

    public JobRepository(HomeManagementDbContext db) => _db = db;

    public async Task<JobStatus?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.Jobs
            .Include(j => j.MachineResults)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task<PagedResult<JobSummary>> QueryAsync(JobQuery query, CancellationToken ct = default)
    {
        IQueryable<JobEntity> q = _db.Jobs;

        if (query.Type.HasValue)
            q = q.Where(j => j.Type == query.Type.Value);
        if (query.State.HasValue)
            q = q.Where(j => j.State == query.State.Value);
        if (query.FromUtc.HasValue)
            q = q.Where(j => j.SubmittedUtc >= query.FromUtc.Value);
        if (query.ToUtc.HasValue)
            q = q.Where(j => j.SubmittedUtc <= query.ToUtc.Value);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(j => j.SubmittedUtc)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        var summaries = items.Select(e => new JobSummary(
            new JobId(e.Id), e.Name, e.Type, e.State,
            e.SubmittedUtc, e.CompletedUtc,
            e.TotalTargets,
            e.MachineResults.Count(r => r.Success),
            e.MachineResults.Count(r => !r.Success))).ToList();

        return new PagedResult<JobSummary>(summaries, total, query.Page, query.PageSize);
    }

    public async Task AddAsync(JobStatus job, CancellationToken ct = default)
    {
        var entity = ToEntity(job);
        await _db.Jobs.AddAsync(entity, ct);
    }

    public Task UpdateAsync(JobStatus job, CancellationToken ct = default)
    {
        var entity = ToEntity(job);
        _db.Jobs.Update(entity);
        return Task.CompletedTask;
    }

    public async Task AddMachineResultAsync(Guid jobId, JobMachineResult result, CancellationToken ct = default)
    {
        var entity = new JobMachineResultEntity
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            MachineId = result.MachineId,
            MachineName = result.MachineName,
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
            DurationMs = (long)result.Duration.TotalMilliseconds
        };
        await _db.JobMachineResults.AddAsync(entity, ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }

    private static JobStatus ToDomain(JobEntity e) => new(
        new JobId(e.Id),
        e.Name,
        e.Type,
        e.State,
        e.SubmittedUtc,
        e.StartedUtc,
        e.CompletedUtc,
        e.TotalTargets,
        e.CompletedTargets,
        e.FailedTargets,
        e.MachineResults.Select(r => new JobMachineResult(
            r.MachineId, r.MachineName, r.Success, r.ErrorMessage,
            TimeSpan.FromMilliseconds(r.DurationMs))).ToList(),
        e.DefinitionJson);

    private static JobEntity ToEntity(JobStatus j) => new()
    {
        Id = j.Id.Value,
        Name = j.Name,
        Type = j.Type,
        State = j.State,
        SubmittedUtc = j.SubmittedUtc,
        StartedUtc = j.StartedUtc,
        CompletedUtc = j.CompletedUtc,
        TotalTargets = j.TotalTargets,
        CompletedTargets = j.CompletedTargets,
        FailedTargets = j.FailedTargets,
        DefinitionJson = j.DefinitionJson,
    };
}
