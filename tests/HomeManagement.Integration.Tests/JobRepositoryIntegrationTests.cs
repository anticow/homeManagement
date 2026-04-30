using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Data;
using HomeManagement.Data.Entities;
using HomeManagement.Data.Repositories;

namespace HomeManagement.Integration.Tests;

[Collection("SqlServer")]
public sealed class JobRepositoryIntegrationTests : IDisposable
{
    private readonly HomeManagementDbContext _context;
    private readonly JobRepository _sut;

    public JobRepositoryIntegrationTests(SqlServerFixture fixture)
    {
        _context = fixture.CreateContext();
        _sut = new JobRepository(_context);
    }

    [Fact]
    public async Task AddAsync_And_GetByIdAsync_RoundTrip()
    {
        var status = CreateJobStatus();

        await _sut.AddAsync(status);
        await _context.SaveChangesAsync();

        var retrieved = await _sut.GetByIdAsync(status.Id.Value);
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be(status.Name);
        retrieved.State.Should().Be(JobState.Queued);
    }

    [Fact]
    public async Task GetByIdempotencyKeyAsync_FindsMatchingJob()
    {
        var key = Guid.NewGuid();
        var status = CreateJobStatus(idempotencyKey: key);

        await _sut.AddAsync(status);
        await _context.SaveChangesAsync();

        var found = await _sut.GetByIdempotencyKeyAsync(key);
        found.Should().NotBeNull();
        found!.Id.Value.Should().Be(status.Id.Value);
    }

    [Fact]
    public async Task GetByIdempotencyKeyAsync_ReturnsNull_WhenNoMatch()
    {
        var result = await _sut.GetByIdempotencyKeyAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task AddMachineResultAsync_PersistsResult()
    {
        var status = CreateJobStatus();
        await _sut.AddAsync(status);
        await _context.SaveChangesAsync();

        var result = new JobMachineResult(Guid.NewGuid(), "test-host", true, null, TimeSpan.FromSeconds(5));
        await _sut.AddMachineResultAsync(status.Id.Value, result);
        await _context.SaveChangesAsync();

        var retrieved = await _sut.GetByIdAsync(status.Id.Value);
        retrieved!.MachineResults.Should().HaveCount(1);
        retrieved.MachineResults[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task QueryAsync_FiltersByState()
    {
        var queued = CreateJobStatus(state: JobState.Queued);
        var completed = CreateJobStatus(state: JobState.Completed);
        await _sut.AddAsync(queued);
        await _sut.AddAsync(completed);
        await _context.SaveChangesAsync();

        var query = new JobQuery(State: JobState.Queued, Page: 1, PageSize: 100);
        var result = await _sut.QueryAsync(query);

        result.Items.Should().Contain(j => j.Id.Value == queued.Id.Value);
        result.Items.Should().NotContain(j => j.Id.Value == completed.Id.Value);
    }

    [Fact]
    public async Task QueryAsync_FiltersByType()
    {
        var patchScan = CreateJobStatus(type: JobType.PatchScan);
        var serviceCtl = CreateJobStatus(type: JobType.ServiceControl);
        await _sut.AddAsync(patchScan);
        await _sut.AddAsync(serviceCtl);
        await _context.SaveChangesAsync();

        var query = new JobQuery(Type: JobType.PatchScan, Page: 1, PageSize: 100);
        var result = await _sut.QueryAsync(query);

        result.Items.Should().Contain(j => j.Id.Value == patchScan.Id.Value);
        result.Items.Should().NotContain(j => j.Id.Value == serviceCtl.Id.Value);
    }

    [Fact]
    public async Task UpdateAsync_PersistsChanges()
    {
        var status = CreateJobStatus();
        await _sut.AddAsync(status);
        await _context.SaveChangesAsync();

        var updated = status with { State = JobState.Running, StartedUtc = DateTime.UtcNow };
        await _sut.UpdateAsync(updated);
        await _context.SaveChangesAsync();

        var retrieved = await _sut.GetByIdAsync(status.Id.Value);
        retrieved!.State.Should().Be(JobState.Running);
        retrieved.StartedUtc.Should().NotBeNull();
    }

    private static JobStatus CreateJobStatus(
        JobState state = JobState.Queued,
        JobType type = JobType.PatchScan,
        Guid? idempotencyKey = null)
    {
        var definitionJson = idempotencyKey.HasValue
            ? $"{{\"TargetMachineIds\":[],\"Parameters\":null,\"MaxParallelism\":5,\"IdempotencyKey\":\"{idempotencyKey.Value}\"}}"
            : "{\"TargetMachineIds\":[],\"Parameters\":null,\"MaxParallelism\":5}";

        return new JobStatus(
            Id: JobId.New(),
            Name: $"test-job-{Guid.NewGuid():N}"[..20],
            Type: type,
            State: state,
            SubmittedUtc: DateTime.UtcNow,
            StartedUtc: null,
            CompletedUtc: state == JobState.Completed ? DateTime.UtcNow : null,
            TotalTargets: 1,
            CompletedTargets: 0,
            FailedTargets: 0,
            MachineResults: [],
            DefinitionJson: definitionJson);
    }

    public void Dispose() => _context.Dispose();
}
