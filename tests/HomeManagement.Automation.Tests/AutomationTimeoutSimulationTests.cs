using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Automation;
using HomeManagement.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Automation.Tests;

/// <summary>
/// Tests for ansible handoff timeout simulation using a mock process runner.
/// These tests are in a separate class to avoid DI container conflicts with the standard test suite.
/// </summary>
public sealed class AutomationTimeoutSimulationTests : IAsyncLifetime, IDisposable
{
    private string _dbPath = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hm_timeout_{Guid.NewGuid():N}.db");

        var collection = new ServiceCollection();
        collection.AddDbContext<HomeManagementDbContext>(options => options.UseSqlite($"DataSource={_dbPath}"));
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

        // Replace GuardedAnsibleHandoffService with a direct implementation that bypasses
        // filesystem path resolution (which fails in CI where ansible is not present).
        var handoffDescriptor = collection.FirstOrDefault(d => d.ServiceType == typeof(IAnsibleHandoffService));
        if (handoffDescriptor != null) collection.Remove(handoffDescriptor);
        collection.AddScoped<IAnsibleHandoffService, DirectProcessHandoffService>();

        collection.AddScoped<IAuditLogger, FakeAuditLogger>();

        _services = collection.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeManagementDbContext>();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=10000;");
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    public void Dispose()
    {
        _services.Dispose();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
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
    /// Bypasses filesystem path resolution in GuardedAnsibleHandoffService, which fails in CI
    /// where no ansible directory is present. Calls the process runner directly so the slow mock
    /// process runner can exercise the timeout/cancellation path.
    /// </summary>
    private sealed class DirectProcessHandoffService : IAnsibleHandoffService
    {
        private readonly IProcessRunner _processRunner;

        public DirectProcessHandoffService(IProcessRunner processRunner) => _processRunner = processRunner;

        public async Task<AnsibleHandoffExecutionResult> ExecuteAsync(AnsibleHandoffRunRequest request, CancellationToken ct = default)
        {
            if (!request.ApproveAndRun)
                return new AnsibleHandoffExecutionResult(false, request.Operation, "", "", DateTime.UtcNow, DateTime.UtcNow, null, "", "", "ApproveAndRun must be true.");

            var startedUtc = DateTime.UtcNow;

            // Replicate the timeout CTS logic from GuardedAnsibleHandoffService
            using var timeoutCts = request.ExecutionTimeoutSeconds.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (timeoutCts != null && request.ExecutionTimeoutSeconds.HasValue)
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.ExecutionTimeoutSeconds.Value));
            var operationCt = timeoutCts?.Token ?? ct;

            var result = await _processRunner.RunAsync("ansible-playbook", "add-k3s-worker.yml", "/tmp", operationCt);

            if (result.WasCancelled)
                return new AnsibleHandoffExecutionResult(
                    false, request.Operation, "add-k3s-worker.yml",
                    "ansible-playbook add-k3s-worker.yml", startedUtc, DateTime.UtcNow,
                    null, "", "", "Ansible handoff execution was cancelled or timed out.", TimedOut: true, Cancelled: true);

            return new AnsibleHandoffExecutionResult(
                result.ExitCode == 0, request.Operation, "add-k3s-worker.yml",
                "ansible-playbook add-k3s-worker.yml", startedUtc, DateTime.UtcNow,
                result.ExitCode, result.StdOut, result.StdErr,
                result.ExitCode == 0 ? null : "non-zero exit");
        }
    }

    /// <summary>
    /// Mock process runner that simulates a long-running process for deterministic timeout testing.
    /// </summary>
    private sealed class SlowMockProcessRunner : IProcessRunner
    {
        private readonly int _delayMs;

        public SlowMockProcessRunner(int delayMs) => _delayMs = delayMs;

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
