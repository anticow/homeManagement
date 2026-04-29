using System.Net;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.AI.Abstractions.Contracts;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data.Repositories;
using HomeManagement.Automation;
using HomeManagement.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Automation.Tests;

public sealed class AutomationSummaryContractTests : IAsyncLifetime, IDisposable
{
    private string _dbPath = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hm_summary_{Guid.NewGuid():N}.db");

        var collection = new ServiceCollection();
        collection.AddDbContext<HomeManagementDbContext>(options => options.UseSqlite($"DataSource={_dbPath}"));
        collection.AddLogging();

        collection.AddOptions<AutomationOptions>()
            .Configure(options => options.Enabled = true);

        new AutomationModuleRegistration().Register(collection);
        collection.AddScoped<IAutomationRunRepository, AutomationRunRepository>();
        collection.AddScoped<IPlanRepository, PlanRepository>();

        collection.AddScoped<IInventoryService, FakeInventoryService>();
        collection.AddScoped<IServiceController, FakeServiceController>();
        collection.AddScoped<IAuditLogger, FakeAuditLogger>();
        collection.AddSingleton<FakeLlmClient>();
        collection.AddSingleton<ILLMClient>(sp => sp.GetRequiredService<FakeLlmClient>());

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
    public async Task HealthReport_WithSuccessfulLlm_IncludesAiSummaryBlock()
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var llm = scope.ServiceProvider.GetRequiredService<FakeLlmClient>();
        llm.Mode = FakeLlmMode.Success;
        llm.Content = "Fleet is healthy. No immediate risk detected.";

        var runId = await engine.StartHealthReportAsync(new HealthReportRunRequest(Tag: "prod"));
        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(8));

        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.OutputMarkdown.Should().NotBeNull();
        run.OutputMarkdown!.Should().Contain("## AI Summary");
        run.OutputMarkdown.Should().Contain("Fleet is healthy. No immediate risk detected.");

        run.Steps.Should().Contain(s => s.Name == "llm_summarize_health" && s.State == AutomationStepState.Completed);
    }

    [Fact]
    public async Task HealthReport_WhenLlmUnavailable_CompletesWithoutAiSummaryAndMarksStepFailed()
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var llm = scope.ServiceProvider.GetRequiredService<FakeLlmClient>();
        llm.Mode = FakeLlmMode.Failure;

        var runId = await engine.StartHealthReportAsync(new HealthReportRunRequest(Tag: "prod"));
        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(8));

        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.OutputMarkdown.Should().NotBeNull();
        run.OutputMarkdown!.Should().NotContain("## AI Summary");

        run.Steps.Should().Contain(s => s.Name == "llm_summarize_health" && s.State == AutomationStepState.Failed);
        llm.Attempts.Should().Be(2);
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

    private enum FakeLlmMode
    {
        Success,
        Failure
    }

    private sealed class FakeLlmClient : ILLMClient
    {
        public FakeLlmMode Mode { get; set; } = FakeLlmMode.Success;
        public string Content { get; set; } = "ok";
        public int Attempts { get; private set; }

        public Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default)
        {
            Attempts++;

            if (Mode == FakeLlmMode.Success)
            {
                return Task.FromResult(new LLMGenerationResult(
                    Success: true,
                    Model: "fake-model",
                    Content: Content,
                    PromptTokens: 12,
                    CompletionTokens: 15,
                    Latency: TimeSpan.FromMilliseconds(20)));
            }

            return Task.FromResult(new LLMGenerationResult(
                Success: false,
                Model: "fake-model",
                Content: string.Empty,
                PromptTokens: null,
                CompletionTokens: null,
                Latency: TimeSpan.FromMilliseconds(20),
                Error: "provider unavailable"));
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

        private static Machine BuildMachine()
        {
            return new Machine(
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
    }

    private sealed class FakeServiceController : IServiceController
    {
        public Task<ServiceInfo> GetStatusAsync(MachineTarget target, ServiceName serviceName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default)
        {
            IReadOnlyList<ServiceInfo> services =
            [
                new ServiceInfo(ServiceName.Create("sshd"), "OpenSSH", ServiceState.Running, ServiceStartupType.Automatic, 4242, TimeSpan.FromHours(10), []),
                new ServiceInfo(ServiceName.Create("cron"), "Cron", ServiceState.Running, ServiceStartupType.Automatic, 4243, TimeSpan.FromHours(9), [])
            ];

            return Task.FromResult(services);
        }

        public async IAsyncEnumerable<ServiceInfo> ListServicesStreamAsync(MachineTarget target, ServiceFilter? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var list = await ListServicesAsync(target, filter, ct);
            foreach (var item in list)
            {
                yield return item;
            }
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
