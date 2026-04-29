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

public sealed class AutomationEnsureServiceRunningTests : IAsyncLifetime, IDisposable
{
    private string _dbPath = null!;
    private ServiceProvider _services = null!;
    private ConfigurableServiceController _serviceController = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hm_svc_{Guid.NewGuid():N}.db");

        _serviceController = new ConfigurableServiceController();

        var collection = new ServiceCollection();
        collection.AddDbContext<HomeManagementDbContext>(options => options.UseSqlite($"DataSource={_dbPath}"));
        collection.AddLogging();

        collection.AddOptions<AutomationOptions>()
            .Configure(options => options.Enabled = true);

        new AutomationModuleRegistration().Register(collection);
        collection.AddScoped<IAutomationRunRepository, AutomationRunRepository>();
        collection.AddScoped<IPlanRepository, PlanRepository>();

        collection.AddScoped<IInventoryService, FakeInventoryService>();
        collection.AddSingleton(_serviceController);
        collection.AddScoped<IServiceController>(sp => sp.GetRequiredService<ConfigurableServiceController>());
        collection.AddScoped<IAuditLogger, FakeAuditLogger>();
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
    public async Task EnsureServiceRunning_WhenStoppedAndRestartEnabled_CompletesSuccessfully()
    {
        _serviceController.InitialState = ServiceState.Stopped;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runId = await engine.StartEnsureServiceRunningAsync(new EnsureServiceRunningRunRequest(
            ServiceName: "sshd",
            Tag: "prod",
            AttemptRestart: true));

        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(10));

        run.WorkflowName.Should().Be("service.ensure_running");
        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.MachineResults.Should().HaveCount(1);
        run.MachineResults[0].Success.Should().BeTrue();

        run.OutputMarkdown.Should().Contain("Service Ensure Running Report");
        run.Steps.Should().Contain(s => s.Name == "ensure_service_running" && s.State == AutomationStepState.Completed);
        _serviceController.ControlCalls.Should().Be(1);
    }

    [Fact]
    public async Task EnsureServiceRunning_WhenStoppedAndRestartDisabled_RecordsFailureWithoutControlCall()
    {
        _serviceController.InitialState = ServiceState.Stopped;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runId = await engine.StartEnsureServiceRunningAsync(new EnsureServiceRunningRunRequest(
            ServiceName: "sshd",
            Tag: "prod",
            AttemptRestart: false));

        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(10));

        run.WorkflowName.Should().Be("service.ensure_running");
        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.MachineResults.Should().HaveCount(1);
        run.MachineResults[0].Success.Should().BeFalse();
        run.MachineResults[0].ErrorMessage.Should().Contain("AttemptRestart=false");

        run.Steps.Should().Contain(s => s.Name == "ensure_service_running" && s.State == AutomationStepState.Completed);
        _serviceController.ControlCalls.Should().Be(0);
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

    private sealed class FakeLlmClient : ILLMClient
    {
        public Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new LLMGenerationResult(
                Success: true,
                Model: "fake",
                Content: "ok",
                PromptTokens: 1,
                CompletionTokens: 1,
                Latency: TimeSpan.FromMilliseconds(5)));
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
                Hardware = new HardwareInfo(CpuCores: 2, RamBytes: 4L * 1024 * 1024 * 1024, Disks: [new DiskInfo("/", 100, 60)], Architecture: "x64")
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

    private sealed class ConfigurableServiceController : IServiceController
    {
        public ServiceState InitialState { get; set; } = ServiceState.Stopped;
        public int ControlCalls { get; private set; }

        public Task<ServiceInfo> GetStatusAsync(MachineTarget target, ServiceName serviceName, CancellationToken ct = default)
        {
            return Task.FromResult(new ServiceInfo(
                serviceName,
                serviceName.Value,
                InitialState,
                ServiceStartupType.Automatic,
                null,
                TimeSpan.FromHours(1),
                []));
        }

        public Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default)
        {
            IReadOnlyList<ServiceInfo> services =
            [
                new ServiceInfo(ServiceName.Create("sshd"), "OpenSSH", ServiceState.Running, ServiceStartupType.Automatic, 4242, TimeSpan.FromHours(10), [])
            ];
            return Task.FromResult(services);
        }

        public async IAsyncEnumerable<ServiceInfo> ListServicesStreamAsync(MachineTarget target, ServiceFilter? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var list = await ListServicesAsync(target, filter, ct);
            foreach (var item in list)
                yield return item;
        }

        public Task<ServiceActionResult> ControlAsync(MachineTarget target, ServiceName serviceName, ServiceAction action, CancellationToken ct = default)
        {
            ControlCalls++;
            return Task.FromResult(new ServiceActionResult(
                target.MachineId,
                serviceName,
                action,
                Success: true,
                ResultingState: ServiceState.Running,
                ErrorMessage: null,
                Duration: TimeSpan.FromMilliseconds(20)));
        }

        public Task<IReadOnlyList<ServiceActionResult>> BulkControlAsync(IReadOnlyList<MachineTarget> targets, ServiceName serviceName, ServiceAction action, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(new PagedResult<AuditEvent>([], 0, 1, 50));
        public Task<long> CountAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(0L);
        public Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => Task.CompletedTask;
    }
}
