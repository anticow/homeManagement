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
/// Dedicated execution-dispatch tests for Phase 3.
/// Validates plan status transitions without using HTTP endpoints.
/// </summary>
public sealed class AutomationPlanExecutionTests : IAsyncLifetime, IDisposable
{
    private string _dbPath = null!;
    private ServiceProvider _services = null!;
    private FakePlannerAndSummaryLlmClient _llmClient = null!;
    private ControlledInventoryService _inventory = null!;
    private FakePatchService _patchService = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hm_plan_exec_{Guid.NewGuid():N}.db");
        _llmClient = new FakePlannerAndSummaryLlmClient();
        _inventory = new ControlledInventoryService();
        _patchService = new FakePatchService();

        var collection = new ServiceCollection();
        collection.AddDbContext<HomeManagementDbContext>(options =>
            options.UseSqlite($"DataSource={_dbPath}"));
        collection.AddLogging();

        collection.AddOptions<AutomationOptions>()
            .Configure(options => options.Enabled = true);

        new AutomationModuleRegistration().Register(collection);

        collection.AddSingleton<ILLMClient>(_llmClient);
        collection.AddSingleton(_inventory);
        collection.AddSingleton(_patchService);
        collection.AddScoped<IInventoryService>(sp => sp.GetRequiredService<ControlledInventoryService>());
        collection.AddScoped<IPatchService>(sp => sp.GetRequiredService<FakePatchService>());
        collection.AddScoped<IServiceController, FakeServiceController>();
        collection.AddScoped<IAuditLogger, FakeAuditLogger>();

        _services = collection.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeManagementDbContext>();
        await db.Database.EnsureCreatedAsync();
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

    [Fact]
    public async Task ApprovePlan_Transitions_Approved_Executing_Completed()
    {
        _inventory.ThrowOnQuery = false;
        _llmClient.PlannerContent = """
            {
              "steps": [
                {
                  "name": "metrics",
                  "kind": "GatherMetrics",
                  "description": "collect",
                  "parameters": { "tag": "prod", "maxTargets": "5" }
                }
              ]
            }
            """;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var plan = await engine.CreatePlanAsync(new CreatePlanRequest("Collect health metrics"));
        var approved = await engine.ApprovePlanAsync(plan.Id, new ApprovePlanRequest(plan.PlanHash));

        approved.Status.Should().Be(PlanStatus.Approved);

        var observed = await ObservePlanStatusesAsync(engine, plan.Id, TimeSpan.FromSeconds(20));

        observed.Should().Contain(PlanStatus.Executing);
        observed.Last().Should().Be(PlanStatus.Completed);
    }

    [Fact]
    public async Task ApprovePlan_Transitions_Approved_Executing_Failed_WhenExecutionFails()
    {
        _inventory.ThrowOnQuery = true;
        _llmClient.PlannerContent = """
            {
              "steps": [
                {
                  "name": "metrics",
                  "kind": "GatherMetrics",
                  "description": "collect",
                  "parameters": { "tag": "prod" }
                }
              ]
            }
            """;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var plan = await engine.CreatePlanAsync(new CreatePlanRequest("Collect health metrics"));
        var approved = await engine.ApprovePlanAsync(plan.Id, new ApprovePlanRequest(plan.PlanHash));

        approved.Status.Should().Be(PlanStatus.Approved);

        var observed = await ObservePlanStatusesAsync(engine, plan.Id, TimeSpan.FromSeconds(20));

        observed.Should().Contain(PlanStatus.Executing);
        observed.Last().Should().Be(PlanStatus.Failed);
    }

    [Fact]
    public async Task ApprovePlan_ApplyPatch_WithTargetMachineIds_UsesMappedTargetsAndDryRun()
    {
        _inventory.ThrowOnQuery = false;
        _patchService.Reset();

        _llmClient.PlannerContent = $$"""
                        {
                            "steps": [
                                {
                                    "name": "patch",
                                    "kind": "ApplyPatch",
                                    "description": "apply patches",
                                    "parameters": {
                                        "targetMachineIds": "{{ControlledInventoryService.DevMachineId}}",
                                        "maxTargets": "5",
                                        "dryRun": "true",
                                        "allowReboot": "true"
                                    }
                                }
                            ]
                        }
                        """;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var plan = await engine.CreatePlanAsync(new CreatePlanRequest("Patch selected machine"));
        var approved = await engine.ApprovePlanAsync(plan.Id, new ApprovePlanRequest(plan.PlanHash));
        approved.Status.Should().Be(PlanStatus.Approved);

        var observed = await ObservePlanStatusesAsync(engine, plan.Id, TimeSpan.FromSeconds(20));
        observed.Last().Should().Be(PlanStatus.Completed);

        var run = await GetLatestRunAsync(engine);
        run.WorkflowName.Should().Be("fleet.patch_all");
        run.MachineResults.Should().HaveCount(1);
        run.MachineResults[0].MachineId.Should().Be(ControlledInventoryService.DevMachineId);
        _patchService.ApplyCalls.Should().Be(0, "dryRun=true should bypass patch apply execution");
    }

    [Fact]
    public async Task ApprovePlan_ApplyPatch_ForwardsAllowRebootOption()
    {
        _inventory.ThrowOnQuery = false;
        _patchService.Reset();

        _llmClient.PlannerContent = """
                        {
                            "steps": [
                                {
                                    "name": "patch",
                                    "kind": "ApplyPatch",
                                    "description": "apply patches",
                                    "parameters": {
                                        "tag": "env",
                                        "maxTargets": "1",
                                        "dryRun": "false",
                                        "allowReboot": "true"
                                    }
                                }
                            ]
                        }
                        """;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var plan = await engine.CreatePlanAsync(new CreatePlanRequest("Patch by tag"));
        var approved = await engine.ApprovePlanAsync(plan.Id, new ApprovePlanRequest(plan.PlanHash));
        approved.Status.Should().Be(PlanStatus.Approved);

        var observed = await ObservePlanStatusesAsync(engine, plan.Id, TimeSpan.FromSeconds(20));
        observed.Last().Should().Be(PlanStatus.Completed);

        _patchService.ApplyCalls.Should().BeGreaterThan(0);
        _patchService.LastAllowReboot.Should().BeTrue();
    }

    private static async Task<List<PlanStatus>> ObservePlanStatusesAsync(
        IAutomationEngine engine,
        WorkflowPlanId planId,
        TimeSpan timeout)
    {
        var stop = DateTime.UtcNow + timeout;
        var statuses = new List<PlanStatus>();

        while (DateTime.UtcNow < stop)
        {
            var plan = await engine.GetPlanAsync(planId);
            plan.Should().NotBeNull();

            var status = plan!.Status;
            if (statuses.Count == 0 || statuses[^1] != status)
            {
                statuses.Add(status);
            }

            if (status is PlanStatus.Completed or PlanStatus.Failed)
            {
                return statuses;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Plan {planId.Value} did not reach terminal status within {timeout}.");
    }

    private static async Task<AutomationRun> GetLatestRunAsync(IAutomationEngine engine)
    {
        var runs = await engine.ListRunsAsync(1, 10);
        runs.Count.Should().BeGreaterThan(0);
        var summary = runs[0];
        var run = await engine.GetRunAsync(summary.Id);
        run.Should().NotBeNull();
        return run!;
    }

    private void DeleteTempDb()
    {
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class FakePlannerAndSummaryLlmClient : ILLMClient
    {
        public string PlannerContent { get; set; } = "{\"steps\":[]}";

        public Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt)
                && request.SystemPrompt.Contains("structured workflow plan", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new LLMGenerationResult(
                    Success: true,
                    Model: "fake-planner",
                    Content: PlannerContent,
                    PromptTokens: 32,
                    CompletionTokens: 72,
                    Latency: TimeSpan.FromMilliseconds(10)));
            }

            return Task.FromResult(new LLMGenerationResult(
                Success: true,
                Model: "fake-summary",
                Content: "Fleet looks healthy.",
                PromptTokens: 12,
                CompletionTokens: 18,
                Latency: TimeSpan.FromMilliseconds(10)));
        }
    }

    private sealed class ControlledInventoryService : IInventoryService
    {
        public static readonly Guid ProdMachineId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public static readonly Guid DevMachineId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        public bool ThrowOnQuery { get; set; }

        public Task<Machine> AddAsync(MachineCreateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Machine> UpdateAsync(Guid id, MachineUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BatchRemoveAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Machine?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Machine?>(null);

        public Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default)
        {
            if (ThrowOnQuery)
            {
                throw new InvalidOperationException("Simulated inventory failure while resolving targets.");
            }

            var machines = new List<Machine>
            {
                BuildMachine(ProdMachineId, "node-prod-01", "prod"),
                BuildMachine(DevMachineId, "node-dev-01", "dev")
            }
            .Take(query.PageSize)
            .ToList();

            return Task.FromResult(new PagedResult<Machine>(
                Items: machines,
                TotalCount: machines.Count,
                Page: 1,
                PageSize: query.PageSize));
        }

        public async Task<Machine> RefreshMetadataAsync(Guid id, CancellationToken ct = default)
        {
            await Task.Delay(120, ct);
            return BuildMachine(id, id == ProdMachineId ? "node-prod-01" : "node-dev-01", id == ProdMachineId ? "prod" : "dev") with
            {
                Hardware = new HardwareInfo(CpuCores: 4, RamBytes: 8L * 1024 * 1024 * 1024, Disks: [new DiskInfo("/", 100, 50)], Architecture: "x64")
            };
        }

        public Task<IReadOnlyList<Machine>> DiscoverAsync(CidrRange range, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ImportAsync(Stream csvStream, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExportAsync(MachineQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => throw new NotSupportedException();

        private static Machine BuildMachine(Guid id, string hostname, string env) =>
            new(
                Id: id,
                Hostname: Hostname.Create(hostname),
                Fqdn: $"{hostname}.local",
                IpAddresses: [IPAddress.Parse("192.168.1.12")],
                OsType: OsType.Linux,
                OsVersion: "Ubuntu 24.04",
                ConnectionMode: MachineConnectionMode.Agent,
                Protocol: TransportProtocol.Agent,
                Port: 9444,
                CredentialId: Guid.NewGuid(),
                State: MachineState.Online,
                Tags: new Dictionary<string, string> { ["env"] = env },
                Hardware: new HardwareInfo(CpuCores: 2, RamBytes: 4L * 1024 * 1024 * 1024, Disks: [new DiskInfo("/", 100, 60)], Architecture: "x64"),
                CreatedUtc: DateTime.UtcNow.AddDays(-1),
                UpdatedUtc: DateTime.UtcNow,
                LastContactUtc: DateTime.UtcNow,
                IsDeleted: false);
    }

    private sealed class FakePatchService : IPatchService
    {
        public int ApplyCalls { get; private set; }
        public bool LastAllowReboot { get; private set; }

        public void Reset()
        {
            ApplyCalls = 0;
            LastAllowReboot = false;
        }

        public Task<IReadOnlyList<PatchInfo>> DetectAsync(MachineTarget target, CancellationToken ct = default)
        {
            IReadOnlyList<PatchInfo> patches =
            [
                new PatchInfo("KB-101", "Security Update", PatchSeverity.Important, PatchCategory.Security, "security", 4096, false, DateTime.UtcNow)
            ];
            return Task.FromResult(patches);
        }

        public async IAsyncEnumerable<PatchInfo> DetectStreamAsync(MachineTarget target, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var items = await DetectAsync(target, ct);
            foreach (var item in items)
            {
                yield return item;
            }
        }

        public Task<PatchResult> ApplyAsync(MachineTarget target, IReadOnlyList<PatchInfo> patches, PatchOptions options, CancellationToken ct = default)
        {
            ApplyCalls++;
            LastAllowReboot = options.AllowReboot;
            return Task.FromResult(new PatchResult(
                target.MachineId,
                Successful: patches.Count,
                Failed: 0,
                Outcomes: patches.Select(p => new PatchOutcome(p.PatchId, PatchInstallState.Installed, null)).ToList(),
                RebootRequired: false,
                Duration: TimeSpan.FromMilliseconds(10)));
        }

        public Task<PatchResult> VerifyAsync(MachineTarget target, IReadOnlyList<string> patchIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(Guid machineId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PatchHistoryEntry>>([]);
        public Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(MachineTarget target, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstalledPatch>>([]);
    }

    private sealed class FakeServiceController : IServiceController
    {
        public Task<ServiceInfo> GetStatusAsync(MachineTarget target, ServiceName serviceName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default)
        {
            IReadOnlyList<ServiceInfo> services =
            [
                new ServiceInfo(ServiceName.Create("sshd"), "OpenSSH", ServiceState.Running, ServiceStartupType.Automatic, 1111, TimeSpan.FromHours(2), []),
                new ServiceInfo(ServiceName.Create("cron"), "Cron", ServiceState.Running, ServiceStartupType.Automatic, 1112, TimeSpan.FromHours(3), [])
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
        public Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(new PagedResult<AuditEvent>([], 0, 1, 50));
        public Task<long> CountAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(0L);
        public Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default) => Task.CompletedTask;
    }
}
