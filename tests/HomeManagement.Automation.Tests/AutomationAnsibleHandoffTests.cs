using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Automation;
using HomeManagement.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Automation.Tests;

public sealed class AutomationAnsibleHandoffTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var collection = new ServiceCollection();
        collection.AddDbContext<HomeManagementDbContext>(options => options.UseSqlite(_connection));
        collection.AddLogging();

        collection.AddOptions<AutomationOptions>()
            .Configure(options => options.Enabled = true);

        new AutomationModuleRegistration().Register(collection);
        collection.AddScoped<IAuditLogger, FakeAuditLogger>();

        _services = collection.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeManagementDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _services.DisposeAsync();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task AnsibleHandoff_DryRun_CompletesWithExecutionSummary()
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runId = await engine.StartAnsibleHandoffAsync(new AnsibleHandoffRunRequest(
            Operation: "k3s.worker.add",
            TargetScope: "worker-*",
            ExtraVarsJson: "{\"node\":\"worker-01\"}",
            DryRun: true,
            ExecutionTimeoutSeconds: 120,
            CancelOnTimeout: true,
            ApproveAndRun: true,
            ApprovedBy: "ops-admin",
            ApprovalReason: "validated in maintenance window",
            ChangeTicket: "CHG-1234"));

        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(10));

        run.WorkflowName.Should().Be("ansible.handoff");
        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.MachineResults.Should().HaveCount(1);
        run.MachineResults[0].Success.Should().BeTrue();
        run.OutputMarkdown.Should().Contain("Ansible Handoff Report");
        run.OutputMarkdown.Should().Contain("k3s.worker.add");
        run.OutputMarkdown.Should().Contain("ExecutionTimeoutSeconds: 120");
        run.OutputMarkdown.Should().Contain("CancelOnTimeout: True");
        run.OutputJson.Should().Contain("CHG-1234");
    }

    [Fact]
    public async Task AnsibleHandoff_MissingApproval_CompletesWithFailureOutcome()
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runId = await engine.StartAnsibleHandoffAsync(new AnsibleHandoffRunRequest(
            Operation: "k3s.worker.add",
            DryRun: true,
            ApproveAndRun: false));

        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(10));

        run.WorkflowName.Should().Be("ansible.handoff");
        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.MachineResults.Should().HaveCount(1);
        run.MachineResults[0].Success.Should().BeFalse();
        run.OutputMarkdown.Should().Contain("Success: False");
        run.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task AnsibleHandoff_AllowlistBypassPayload_CompletesWithFailureOutcome()
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runId = await engine.StartAnsibleHandoffAsync(new AnsibleHandoffRunRequest(
            Operation: "k3s.worker.add && whoami",
            DryRun: true,
            ApproveAndRun: true,
            ApprovedBy: "ops-admin",
            ApprovalReason: "red-team-check"));

        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(10));

        run.WorkflowName.Should().Be("ansible.handoff");
        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.MachineResults.Should().HaveCount(1);
        run.MachineResults[0].Success.Should().BeFalse();
        run.MachineResults[0].ErrorMessage.Should().Contain("not allowlisted");
        run.OutputMarkdown.Should().Contain("Success: False");
    }

    private static async Task<AutomationRun> WaitForRunAsync(IAutomationEngine engine, AutomationRunId runId, TimeSpan timeout)
    {
        var stop = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < stop)
        {
            var run = await engine.GetRunAsync(runId);
            if (run is not null && run.State is AutomationRunStateKind.Completed or AutomationRunStateKind.Failed)
            {
                return run;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Run {runId.Value} did not reach terminal state within {timeout}.");
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(new PagedResult<AuditEvent>([], 0, 1, 50));
        public Task<long> CountAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(0L);
        public Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => Task.CompletedTask;
    }














}

