using FluentAssertions;
using HomeManagement.Data;
using HomeManagement.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Automation.Tests;

public sealed class AutomationRunRepositoryTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection _connection = null!;
    private HomeManagementDbContext _dbContext = null!;
    private AutomationRunRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new HomeManagementDbContext(options);
        await _dbContext.Database.EnsureCreatedAsync();
        _repository = new AutomationRunRepository(_dbContext);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateRunAndGetRun_PersistsRun()
    {
        var runId = Guid.NewGuid();

        await _repository.CreateRunAsync(
            runId,
            "fleet.health_report",
            "{\"tag\":\"env\"}",
            "corr-1");

        var run = await _repository.GetRunAsync(runId);

        run.Should().NotBeNull();
        run!.Id.Should().Be(runId);
        run.WorkflowType.Should().Be("fleet.health_report");
        run.State.Should().Be("Queued");
        run.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public async Task AddStepAndMachineResult_AreAttachedToRun()
    {
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var machineId = Guid.NewGuid();

        await _repository.CreateRunAsync(runId, "fleet.health_report", null, null);
        await _repository.AddStepAsync(runId, stepId, "resolve_targets");
        await _repository.AddMachineResultAsync(
            runId,
            machineId,
            "node-1",
            success: true,
            errorMessage: null,
            resultDataJson: "{\"cpuCores\":4}");

        var run = await _repository.GetRunAsync(runId);

        run.Should().NotBeNull();
        run!.Steps.Should().HaveCount(1);
        run.MachineResults.Should().HaveCount(1);
        run.Steps.First().StepName.Should().Be("resolve_targets");
        run.MachineResults.First().MachineName.Should().Be("node-1");
    }

    [Fact]
    public async Task UpdateRunCompleted_SetsCompletionFields()
    {
        var runId = Guid.NewGuid();

        await _repository.CreateRunAsync(runId, "fleet.health_report", null, null);
        await _repository.UpdateRunCompletedAsync(
            runId,
            state: "Completed",
            completedMachines: 3,
            failedMachines: 1,
            outputJson: "{\"ok\":true}",
            outputMarkdown: "# done");

        var run = await _repository.GetRunAsync(runId);

        run.Should().NotBeNull();
        run!.State.Should().Be("Completed");
        run.CompletedMachines.Should().Be(3);
        run.FailedMachines.Should().Be(1);
        run.OutputJson.Should().Contain("ok");
        run.OutputMarkdown.Should().Contain("done");
        run.CompletedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ListRuns_IsPaged()
    {
        for (var i = 0; i < 5; i++)
        {
            await _repository.CreateRunAsync(Guid.NewGuid(), "fleet.health_report", null, null);
            await Task.Delay(5);
        }

        var page1 = await _repository.ListRunsAsync(1, 2);
        var page2 = await _repository.ListRunsAsync(2, 2);

        page1.Should().HaveCount(2);
        page2.Should().HaveCount(2);

        var page1Ids = page1.Select(x => x.Id).ToHashSet();
        var page2Ids = page2.Select(x => x.Id).ToHashSet();
        page1Ids.Intersect(page2Ids).Should().BeEmpty();
    }
}
