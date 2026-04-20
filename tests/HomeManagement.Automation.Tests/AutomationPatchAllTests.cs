using System.Net;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.AI.Abstractions.Contracts;
using HomeManagement.Automation;
using HomeManagement.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Automation.Tests;

public sealed class AutomationPatchAllTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private FakePatchService _patchService = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        _patchService = new FakePatchService();

        var collection = new ServiceCollection();
        collection.AddDbContext<HomeManagementDbContext>(options => options.UseSqlite(_connection));
        collection.AddLogging();

        collection.AddOptions<AutomationOptions>()
            .Configure(options => options.Enabled = true);

        new AutomationModuleRegistration().Register(collection);

        collection.AddScoped<IInventoryService, FakeInventoryService>();
        collection.AddSingleton(_patchService);
        collection.AddScoped<IPatchService>(sp => sp.GetRequiredService<FakePatchService>());
        collection.AddScoped<IServiceController, FakeServiceController>();
        collection.AddScoped<IAuditLogger, FakeAuditLogger>();
        collection.AddSingleton<ILLMClient, FakeLlmClient>();

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
    public async Task PatchAll_DryRun_CompletesWithoutApplyCalls()
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runId = await engine.StartPatchAllAsync(new PatchAllRunRequest(
            Tag: "prod",
            DryRun: true,
            AllowReboot: false));

        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(10));

        run.WorkflowName.Should().Be("fleet.patch_all");
        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.MachineResults.Should().HaveCount(1);
        run.MachineResults[0].Success.Should().BeTrue();
        run.OutputMarkdown.Should().Contain("Fleet Patch All Report");
        _patchService.ApplyCalls.Should().Be(0);
    }

    [Fact]
    public async Task PatchAll_ApplyMode_CompletesAndInvokesApply()
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var runId = await engine.StartPatchAllAsync(new PatchAllRunRequest(
            Tag: "prod",
            DryRun: false,
            AllowReboot: false));

        var run = await WaitForRunAsync(engine, runId, TimeSpan.FromSeconds(10));

        run.WorkflowName.Should().Be("fleet.patch_all");
        run.State.Should().Be(AutomationRunStateKind.Completed);
        run.MachineResults.Should().HaveCount(1);
        run.MachineResults[0].Success.Should().BeTrue();
        _patchService.ApplyCalls.Should().BeGreaterThan(0);
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

    private sealed class FakePatchService : IPatchService
    {
        public int ApplyCalls { get; private set; }

        public Task<IReadOnlyList<PatchInfo>> DetectAsync(MachineTarget target, CancellationToken ct = default)
        {
            IReadOnlyList<PatchInfo> patches =
            [
                new PatchInfo("KB-100", "Security Patch", PatchSeverity.Important, PatchCategory.Security, "security", 1024, false, DateTime.UtcNow)
            ];
            return Task.FromResult(patches);
        }

        public async IAsyncEnumerable<PatchInfo> DetectStreamAsync(MachineTarget target, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var items = await DetectAsync(target, ct);
            foreach (var item in items)
                yield return item;
        }

        public Task<PatchResult> ApplyAsync(MachineTarget target, IReadOnlyList<PatchInfo> patches, PatchOptions options, CancellationToken ct = default)
        {
            ApplyCalls++;
            return Task.FromResult(new PatchResult(
                target.MachineId,
                Successful: patches.Count,
                Failed: 0,
                Outcomes: patches.Select(p => new PatchOutcome(p.PatchId, PatchInstallState.Installed, null)).ToList(),
                RebootRequired: false,
                Duration: TimeSpan.FromMilliseconds(25)));
        }

        public Task<PatchResult> VerifyAsync(MachineTarget target, IReadOnlyList<string> patchIds, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(Guid machineId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PatchHistoryEntry>>([]);

        public Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(MachineTarget target, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InstalledPatch>>([]);
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
        public Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ServiceInfo>>([]);
        public async IAsyncEnumerable<ServiceInfo> ListServicesStreamAsync(MachineTarget target, ServiceFilter? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<ServiceActionResult> ControlAsync(MachineTarget target, ServiceName serviceName, ServiceAction action, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServiceActionResult>> BulkControlAsync(IReadOnlyList<MachineTarget> targets, ServiceName serviceName, ServiceAction action, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(new PagedResult<AuditEvent>([], 0, 1, 50));
        public Task<long> CountAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(0L);
        public Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeLlmClient : ILLMClient
    {
        public Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new LLMGenerationResult(true, "fake", "ok", 1, 1, TimeSpan.FromMilliseconds(5)));
        }
    }
}
