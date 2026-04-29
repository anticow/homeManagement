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

public sealed class AutomationHaosAdapterTests : IAsyncLifetime, IDisposable
{
    private string _dbPath = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hm_haos_{Guid.NewGuid():N}.db");

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
        collection.AddScoped<IPatchService, FakePatchService>();
        collection.AddScoped<IAuditLogger, FakeAuditLogger>();
        collection.AddScoped<IHaosAdapter, FakeHaosAdapter>();
        collection.AddSingleton<ILLMClient, FakeLlmClient>();

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
    public async Task HaosHealthStatus_Run_CompletesWithStatusOutput()
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runId = await engine.StartHaosHealthStatusAsync(new HaosHealthStatusRunRequest("haos-lab"));
        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(10));

        run.WorkflowName.Should().Be("haos.health_status");
        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.OutputMarkdown.Should().Contain("HAOS Health Status Report");
        run.OutputJson.Should().Contain("haos-lab");
        run.Steps.Should().Contain(s => s.Name == "haos_supervisor_status" && s.State == AutomationStepState.Completed);
    }

    [Fact]
    public async Task HaosEntitySnapshot_Run_CompletesWithEntitySummary()
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runId = await engine.StartHaosEntitySnapshotAsync(new HaosEntitySnapshotRunRequest("sensor", 20, "haos-lab"));
        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(10));

        run.WorkflowName.Should().Be("haos.entity_snapshot");
        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.OutputMarkdown.Should().Contain("HAOS Entity Snapshot Report");
        run.OutputMarkdown.Should().Contain("sensor.temp_living");
        run.OutputJson.Should().Contain("entityCount");
        run.Steps.Should().Contain(s => s.Name == "haos_entity_snapshot" && s.State == AutomationStepState.Completed);
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

    private sealed class FakeHaosAdapter : IHaosAdapter
    {
        public Task<HaosSupervisorStatus> GetSupervisorStatusAsync(string? instanceName = null, CancellationToken ct = default)
        {
            return Task.FromResult(new HaosSupervisorStatus(
                InstanceName: string.IsNullOrWhiteSpace(instanceName) ? "haos-default" : instanceName,
                Version: "2026.4.0",
                Health: "Healthy",
                RetrievedUtc: DateTime.UtcNow,
                Metadata: new Dictionary<string, string> { ["mode"] = "read-only" }));
        }

        public Task<IReadOnlyList<HaosEntityState>> GetEntitiesAsync(string? domainFilter = null, int maxEntities = 250, string? instanceName = null, CancellationToken ct = default)
        {
            IReadOnlyList<HaosEntityState> entities =
            [
                new HaosEntityState("sensor.temp_living", "22.1", DateTime.UtcNow, new Dictionary<string, string> { ["unit"] = "C" }),
                new HaosEntityState("sensor.humidity_living", "48", DateTime.UtcNow, new Dictionary<string, string> { ["unit"] = "%" })
            ];

            var filtered = string.IsNullOrWhiteSpace(domainFilter)
                ? entities
                : entities.Where(e => e.EntityId.StartsWith(domainFilter + ".", StringComparison.OrdinalIgnoreCase)).ToList();

            return Task.FromResult<IReadOnlyList<HaosEntityState>>(filtered.Take(maxEntities).ToList());
        }
    }

    private sealed class FakeLlmClient : ILLMClient
    {
        public Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new LLMGenerationResult(true, "fake", "ok", 1, 1, TimeSpan.FromMilliseconds(5)));
        }
    }

    private sealed class FakeInventoryService : IInventoryService
    {
        public Task<Machine> AddAsync(MachineCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Machine> UpdateAsync(Guid id, MachineUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BatchRemoveAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Machine?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Machine?>(null);
        public Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default) => Task.FromResult(new PagedResult<Machine>([], 0, 1, 50));
        public Task<Machine> RefreshMetadataAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Machine>> DiscoverAsync(CidrRange range, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ImportAsync(Stream csvStream, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExportAsync(MachineQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeServiceController : IServiceController
    {
        public Task<ServiceInfo> GetStatusAsync(MachineTarget target, ServiceName serviceName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ServiceInfo>>([]);
        public async IAsyncEnumerable<ServiceInfo> ListServicesStreamAsync(MachineTarget target, ServiceFilter? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<ServiceActionResult> ControlAsync(MachineTarget target, ServiceName serviceName, ServiceAction action, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServiceActionResult>> BulkControlAsync(IReadOnlyList<MachineTarget> targets, ServiceName serviceName, ServiceAction action, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakePatchService : IPatchService
    {
        public Task<IReadOnlyList<PatchInfo>> DetectAsync(MachineTarget target, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PatchInfo>>([]);
        public async IAsyncEnumerable<PatchInfo> DetectStreamAsync(MachineTarget target, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<PatchResult> ApplyAsync(MachineTarget target, IReadOnlyList<PatchInfo> patches, PatchOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PatchResult> VerifyAsync(MachineTarget target, IReadOnlyList<string> patchIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(Guid machineId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PatchHistoryEntry>>([]);
        public Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(MachineTarget target, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstalledPatch>>([]);
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(new PagedResult<AuditEvent>([], 0, 1, 50));
        public Task<long> CountAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(0L);
        public Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => Task.CompletedTask;
    }
}
