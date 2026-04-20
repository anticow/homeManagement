using System.Net;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.AI.Abstractions.Contracts;
using HomeManagement.Automation;
using HomeManagement.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Automation.Tests;

/// <summary>
/// Phase 2 exit-criteria: deterministic summaries under load with proper failure handling.
/// </summary>
public sealed class AutomationConcurrencyTests : IAsyncLifetime, IDisposable
{
    // Use a temp-file database rather than a shared in-memory connection.
    // File-based SQLite allows each EF Core scope to open its own connection
    // handle, preventing the 'database is locked' error that arises when
    // multiple DbContext instances share a single SqliteConnection object.
    private string _dbPath = null!;
    private ServiceProvider _services = null!;
    private ConcurrentFakeLlmClient _llmClient = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hm_conc_{Guid.NewGuid():N}.db");
        _llmClient = new ConcurrentFakeLlmClient();

        var collection = new ServiceCollection();
        collection.AddDbContext<HomeManagementDbContext>(options =>
            options.UseSqlite($"DataSource={_dbPath}"));
        collection.AddLogging();

        collection.AddOptions<AutomationOptions>()
            .Configure(o => o.Enabled = true);

        new AutomationModuleRegistration().Register(collection);

        collection.AddScoped<IInventoryService, FakeInventoryService>();
        collection.AddScoped<IServiceController, FakeServiceController>();
        collection.AddScoped<IAuditLogger, FakeAuditLogger>();
        collection.AddSingleton<ILLMClient>(_llmClient);

        _services = collection.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeManagementDbContext>();
        await db.Database.EnsureCreatedAsync();

        // WAL mode allows concurrent readers and serialised writers without
        // returning SQLITE_BUSY immediately; busy_timeout lets writers queue
        // for up to 10 s before giving up, which absorbs bursts of contention.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=10000;");
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        DeleteTempDb();
    }

    public void Dispose()
    {
        _services.Dispose();
        DeleteTempDb();
    }

    private void DeleteTempDb()
    {
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentRuns_AllCompleteWithAiSummary_AndNoOutputCrossContamination()
    {
        const int runCount = 5;
        _llmClient.Mode = ConcurrentFakeLlmMode.SlowSuccess;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        // Start all runs simultaneously to maximise lock contention on the shared SQLite connection.
        var runIds = await Task.WhenAll(
            Enumerable.Range(0, runCount)
                .Select(_ => engine.StartHealthReportAsync(new HealthReportRunRequest(Tag: "prod"))));

        // All IDs must be distinct — engine allocates a new ID per call.
        runIds.Select(id => id.Value).Should().OnlyHaveUniqueItems("each run must have its own identity");

        // Wait for every run to reach a terminal state.
        var runs = await Task.WhenAll(
            runIds.Select(id => WaitForRunAsync(engine, id, TimeSpan.FromSeconds(30))));

        // Every run must complete successfully.
        runs.Should().AllSatisfy(run =>
        {
            run.State.Should().Be(AutomationRunStateKind.Completed, "no run should be left in a non-terminal state");
            run.OutputMarkdown.Should().NotBeNull();
            run.OutputMarkdown!.Should().Contain("# Fleet Health Report", "output structure must be stable");
            run.OutputMarkdown.Should().Contain("## AI Summary", "LLM summary must be injected for each run");
            run.Steps.Should().Contain(
                s => s.Name == "llm_summarize_health" && s.State == AutomationStepState.Completed,
                "summarize step must reach Completed");
        });

        // No run may contain more than one AI Summary block — guards against output bleed.
        runs.Should().AllSatisfy(run =>
        {
            CountSubstringOccurrences(run.OutputMarkdown!, "## AI Summary")
                .Should().Be(1, "each run must have exactly one AI Summary section");
        });

        // All generated summaries must be structurally distinct (no shared output buffer).
        runs.Select(r => r.OutputMarkdown!).Should().OnlyHaveUniqueItems(
            "concurrent runs must never share or overwrite each other's output");
    }

    [Fact]
    public async Task ConcurrentRuns_WhenLlmThrowsTimeout_AllCompleteWithFallbackAndNoSummaryBlock()
    {
        const int runCount = 4;
        _llmClient.Mode = ConcurrentFakeLlmMode.ThrowTimeout;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runIds = await Task.WhenAll(
            Enumerable.Range(0, runCount)
                .Select(_ => engine.StartHealthReportAsync(new HealthReportRunRequest(Tag: "staging"))));

        var runs = await Task.WhenAll(
            runIds.Select(id => WaitForRunAsync(engine, id, TimeSpan.FromSeconds(30))));

        // Every run must still reach Completed — a failed LLM must never abort the workflow.
        runs.Should().AllSatisfy(run =>
        {
            run.State.Should().Be(AutomationRunStateKind.Completed,
                "LLM timeout must not cause the automation run to fail");
            run.OutputMarkdown.Should().NotBeNull();
            run.OutputMarkdown!.Should().NotContain("## AI Summary",
                "no partial summary may bleed into output when LLM times out");
            run.Steps.Should().Contain(
                s => s.Name == "llm_summarize_health" && s.State == AutomationStepState.Failed,
                "summarize step must be marked Failed on timeout");
        });

        // Engine retries exactly twice per run — total attempts == runCount * 2.
        _llmClient.TotalAttempts.Should().Be(runCount * 2,
            "engine must attempt the LLM exactly 2 times per run (retry policy) even under concurrency");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<AutomationRun> WaitForRunAsync(
        IAutomationEngine engine,
        AutomationRunId runId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var run = await engine.GetRunAsync(runId);
            if (run is not null && run.State is AutomationRunStateKind.Completed or AutomationRunStateKind.Failed)
                return run;

            await Task.Delay(100);
        }

        throw new TimeoutException($"Run {runId.Value} did not reach a terminal state within {timeout}.");
    }

    private static int CountSubstringOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    private enum ConcurrentFakeLlmMode
    {
        SlowSuccess,
        ThrowTimeout
    }

    /// <summary>
    /// Thread-safe LLM stub. Uses <see cref="Interlocked.Increment"/> so concurrent
    /// callers each receive a unique, monotonically increasing call number that is
    /// embedded in the summary text — making cross-run output contamination detectable.
    /// </summary>
    private sealed class ConcurrentFakeLlmClient : ILLMClient
    {
        private int _totalAttempts;

        public ConcurrentFakeLlmMode Mode { get; set; } = ConcurrentFakeLlmMode.SlowSuccess;

        /// <summary>Total <see cref="GenerateAsync"/> invocations across all concurrent callers.</summary>
        public int TotalAttempts => Volatile.Read(ref _totalAttempts);

        public async Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default)
        {
            // Atomically claim a unique attempt number before any await.
            var attemptNumber = Interlocked.Increment(ref _totalAttempts);

            // Simulate realistic LLM latency so thread-interleaving is exercised.
            await Task.Delay(50, ct);

            if (Mode == ConcurrentFakeLlmMode.ThrowTimeout)
                throw new TaskCanceledException($"LLM provider timed out (simulated, attempt {attemptNumber}).");

            return new LLMGenerationResult(
                Success: true,
                Model: "fake-concurrent",
                // The attempt number is unique per call — embeds traceability into each run's output.
                Content: $"Concurrent summary #{attemptNumber}: all fleet services nominal, no critical alerts.",
                PromptTokens: 20,
                CompletionTokens: 14,
                Latency: TimeSpan.FromMilliseconds(50));
        }
    }

    private sealed class FakeInventoryService : IInventoryService
    {
        public Task<Machine> AddAsync(MachineCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Machine> UpdateAsync(Guid id, MachineUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BatchRemoveAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Machine?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Machine?>(null);

        public Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default)
        {
            var machine = BuildMachine();
            return Task.FromResult(new PagedResult<Machine>(
                Items: [machine],
                TotalCount: 1,
                Page: 1,
                PageSize: 50));
        }

        public Task<Machine> RefreshMetadataAsync(Guid id, CancellationToken ct = default)
        {
            var machine = BuildMachine() with
            {
                Id = id,
                Hardware = new HardwareInfo(CpuCores: 4, RamBytes: 8L * 1024 * 1024 * 1024, Disks: [new DiskInfo("/", 100, 50)], Architecture: "x64")
            };
            return Task.FromResult(machine);
        }

        public Task<IReadOnlyList<Machine>> DiscoverAsync(CidrRange range, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ImportAsync(Stream csvStream, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExportAsync(MachineQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => throw new NotSupportedException();

        private static Machine BuildMachine() =>
            new(
                Id: Guid.NewGuid(),
                Hostname: Hostname.Create("node-prod-01"),
                Fqdn: "node-prod-01.local",
                IpAddresses: [IPAddress.Parse("192.168.1.12")],
                OsType: OsType.Linux,
                OsVersion: "Ubuntu 24.04",
                ConnectionMode: MachineConnectionMode.Agent,
                Protocol: TransportProtocol.Agent,
                Port: 9444,
                CredentialId: Guid.NewGuid(),
                State: MachineState.Online,
                Tags: new Dictionary<string, string> { ["env"] = "prod" },
                Hardware: new HardwareInfo(CpuCores: 2, RamBytes: 4L * 1024 * 1024 * 1024, Disks: [new DiskInfo("/", 100, 60)], Architecture: "x64"),
                CreatedUtc: DateTime.UtcNow.AddDays(-2),
                UpdatedUtc: DateTime.UtcNow,
                LastContactUtc: DateTime.UtcNow,
                IsDeleted: false);
    }

    private sealed class FakeServiceController : IServiceController
    {
        public Task<ServiceInfo> GetStatusAsync(MachineTarget target, ServiceName serviceName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default)
        {
            IReadOnlyList<ServiceInfo> services =
            [
                new ServiceInfo(ServiceName.Create("sshd"), "OpenSSH", ServiceState.Running, ServiceStartupType.Automatic, 4242, TimeSpan.FromHours(10), []),
                new ServiceInfo(ServiceName.Create("cron"), "Cron",    ServiceState.Running, ServiceStartupType.Automatic, 4243, TimeSpan.FromHours(9),  [])
            ];
            return Task.FromResult(services);
        }

        public async IAsyncEnumerable<ServiceInfo> ListServicesStreamAsync(
            MachineTarget target,
            ServiceFilter? filter = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var list = await ListServicesAsync(target, filter, ct);
            foreach (var item in list)
                yield return item;
        }

        public Task<ServiceActionResult> ControlAsync(MachineTarget target, ServiceName serviceName, ServiceAction action, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServiceActionResult>> BulkControlAsync(IReadOnlyList<MachineTarget> targets, ServiceName serviceName, ServiceAction action, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default)
            => Task.FromResult(new PagedResult<AuditEvent>([], 0, 1, 50));
        public Task<long> CountAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(0L);
        public Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => Task.CompletedTask;
    }
}
