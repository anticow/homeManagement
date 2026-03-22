using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Impl;

namespace HomeManagement.Orchestration.Tests;

public sealed class JobSchedulerServiceTests : IDisposable
{
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IServiceScope _scope = Substitute.For<IServiceScope>();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IJobRepository _jobRepo = Substitute.For<IJobRepository>();
    private readonly ICorrelationContext _correlation = Substitute.For<ICorrelationContext>();
    private readonly JobSchedulerService _sut;

    public JobSchedulerServiceTests()
    {
        _correlation.CorrelationId.Returns("test-corr");
        _schedulerFactory = new StdSchedulerFactory();
        _scope.ServiceProvider.Returns(_serviceProvider);
        _serviceProvider.GetService(typeof(IJobRepository)).Returns(_jobRepo);
        _scopeFactory.CreateScope().Returns(_scope);

        _sut = new JobSchedulerService(
            _schedulerFactory, _scopeFactory, _correlation,
            NullLogger<JobSchedulerService>.Instance);
    }

    [Fact]
    public async Task SubmitAsync_PersistsJobAndReturnsId()
    {
        var job = CreateJobDefinition();

        var jobId = await _sut.SubmitAsync(job);

        jobId.Value.Should().NotBeEmpty();
        await _jobRepo.Received(1).AddAsync(Arg.Any<JobStatus>(), Arg.Any<CancellationToken>());
        await _jobRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitAsync_WithIdempotencyKey_ReturnsSameId_OnDuplicate()
    {
        var idempotencyKey = Guid.NewGuid();
        var existingJob = new JobStatus(
            new JobId(Guid.NewGuid()), "existing", JobType.PatchScan, JobState.Queued,
            DateTime.UtcNow, null, null, 1, 0, 0, [], null);

        _jobRepo.GetByIdempotencyKeyAsync(idempotencyKey, Arg.Any<CancellationToken>())
            .Returns(existingJob);

        var job = CreateJobDefinition() with { IdempotencyKey = idempotencyKey };
        var result = await _sut.SubmitAsync(job);

        result.Should().Be(existingJob.Id);
        await _jobRepo.DidNotReceive().AddAsync(Arg.Any<JobStatus>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitAsync_WithIdempotencyKey_CreatesNew_WhenNoDuplicate()
    {
        var idempotencyKey = Guid.NewGuid();
        _jobRepo.GetByIdempotencyKeyAsync(idempotencyKey, Arg.Any<CancellationToken>())
            .Returns((JobStatus?)null);

        var job = CreateJobDefinition() with { IdempotencyKey = idempotencyKey };
        var result = await _sut.SubmitAsync(job);

        result.Value.Should().NotBeEmpty();
        await _jobRepo.Received(1).AddAsync(Arg.Any<JobStatus>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsJobStatus()
    {
        var jobId = new JobId(Guid.NewGuid());
        var status = new JobStatus(jobId, "test", JobType.PatchScan, JobState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null, 1, 0, 0, [], null);

        _jobRepo.GetByIdAsync(jobId.Value, Arg.Any<CancellationToken>()).Returns(status);

        var result = await _sut.GetStatusAsync(jobId);

        result.Should().BeSameAs(status);
    }

    [Fact]
    public async Task GetStatusAsync_ThrowsWhenNotFound()
    {
        _jobRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((JobStatus?)null);

        var act = () => _sut.GetStatusAsync(new JobId(Guid.NewGuid()));

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task ListJobsAsync_DelegatesToRepository()
    {
        var query = new JobQuery(Page: 1, PageSize: 10);
        var expected = new PagedResult<JobSummary>([], 0, 1, 10);
        _jobRepo.QueryAsync(query, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.ListJobsAsync(query);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task CancelAsync_UpdatesJobStateAndSaves()
    {
        var jobId = new JobId(Guid.NewGuid());
        var status = new JobStatus(jobId, "job", JobType.PatchScan, JobState.Running,
            DateTime.UtcNow, DateTime.UtcNow, null, 1, 0, 0, [], null);

        _jobRepo.GetByIdAsync(jobId.Value, Arg.Any<CancellationToken>()).Returns(status);

        await _sut.CancelAsync(jobId);

        await _jobRepo.Received(1).UpdateAsync(
            Arg.Is<JobStatus>(s => s.State == JobState.Cancelled),
            Arg.Any<CancellationToken>());
        await _jobRepo.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ProgressStream_IsObservable()
    {
        _sut.ProgressStream.Should().NotBeNull();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var act = () => _sut.Dispose();
        act.Should().NotThrow();
    }

    private static JobDefinition CreateJobDefinition() => new(
        Name: "Test Job",
        Type: JobType.PatchScan,
        TargetMachineIds: [Guid.NewGuid()],
        Parameters: []);

    public void Dispose() => _sut.Dispose();
}
