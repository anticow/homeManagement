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

/// <summary>
/// Tests for ansible handoff timeout simulation using a mock process runner.
/// These tests are in a separate class to avoid DI container conflicts with the standard test suite.
/// </summary>
public sealed class AutomationTimeoutSimulationTests : IAsyncLifetime, IDisposable
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

        // Register mock process runner before module registration
        collection.AddSingleton<IProcessRunner>(_ => new SlowMockProcessRunner(delayMs: 10000));

        // Register module (which attempts to register DefaultProcessRunner, but our mock takes precedence)
        new AutomationModuleRegistration().Register(collection);

        // Ensure mock runner is used by removing and re-adding if necessary
        var processRunnerDescriptor = collection.FirstOrDefault(d => d.ServiceType == typeof(IProcessRunner));
        if (processRunnerDescriptor?.ImplementationInstance?.GetType() != typeof(SlowMockProcessRunner))
        {
            if (processRunnerDescriptor != null)
                collection.Remove(processRunnerDescriptor);
            collection.AddSingleton<IProcessRunner>(_ => new SlowMockProcessRunner(delayMs: 10000));
        }

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
    public async Task AnsibleHandoff_NonDryRunTimeout_CompletesWithTimeoutOutcome()
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runId = await engine.StartAnsibleHandoffAsync(new AnsibleHandoffRunRequest(
            Operation: "k3s.worker.add",
            TargetScope: "worker-*",
            DryRun: false,
            ExecutionTimeoutSeconds: 5,  // 5 second timeout (minimum valid); mock takes 10 seconds
            CancelOnTimeout: true,
            ApproveAndRun: true,
            ApprovedBy: "ops-admin",
            ApprovalReason: "timeout-simulation-test",
            ChangeTicket: "CHG-TIMEOUT-001"));

        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(20));

        run.WorkflowName.Should().Be("ansible.handoff");
        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.MachineResults.Should().HaveCount(1);
        run.MachineResults[0].Success.Should().BeFalse();
        run.MachineResults[0].ErrorMessage.Should().Contain("cancelled or timed out");
        run.OutputMarkdown.Should().Contain("TimedOut: True");
        run.OutputMarkdown.Should().Contain("Cancelled: True");
        run.OutputJson.Should().Contain("\"TimedOut\":true");
        run.OutputJson.Should().Contain("\"Cancelled\":true");
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

    /// <summary>
    /// Mock process runner that simulates a long-running process for deterministic timeout testing.
    /// </summary>
    private sealed class SlowMockProcessRunner : IProcessRunner
    {
        private readonly int _delayMs;

        public SlowMockProcessRunner(int delayMs)
        {
            _delayMs = delayMs;
        }

        public async Task<ProcessResult> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken ct)
        {
            try
            {
                // Simulate a process that takes a long time to complete.
                // Task.Delay respects CancellationToken, so timeout will properly cancel this.
                await Task.Delay(_delayMs, ct);
                return new ProcessResult(ExitCode: 0, StdOut: "Success", StdErr: "", WasCancelled: false);
            }
            catch (OperationCanceledException)
            {
                // When cancelled (e.g., by timeout), report the cancellation.
                return new ProcessResult(ExitCode: null, StdOut: "", StdErr: "", WasCancelled: true);
            }
        }
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(new PagedResult<AuditEvent>([], 0, 1, 50));
        public Task<long> CountAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(0L);
        public Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => Task.CompletedTask;
    }
}
