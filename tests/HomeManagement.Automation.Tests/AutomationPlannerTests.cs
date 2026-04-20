using FluentAssertions;
using HomeManagement.AI.Abstractions.Contracts;
using HomeManagement.Automation;
using HomeManagement.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Automation.Tests;

public sealed class AutomationPlannerTests : IAsyncLifetime, IDisposable
{
    private string _dbPath = null!;
    private ServiceProvider _services = null!;
    private FakePlannerLlmClient _llmClient = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"hm_plan_{Guid.NewGuid():N}.db");
        _llmClient = new FakePlannerLlmClient();

        var collection = new ServiceCollection();
        collection.AddDbContext<HomeManagementDbContext>(options =>
            options.UseSqlite($"DataSource={_dbPath}"));
        collection.AddLogging();

        collection.AddOptions<AutomationOptions>()
            .Configure(options => options.Enabled = true);

        new AutomationModuleRegistration().Register(collection);

        collection.AddSingleton<ILLMClient>(_llmClient);

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
    public async Task CreatePlan_WithAllowedSteps_PersistsPendingApprovalPlan()
    {
        _llmClient.Content = """
            {
              "steps": [
                {
                  "name": "gather",
                  "kind": "GatherMetrics",
                  "description": "Collect metrics",
                  "parameters": { "tag": "prod" }
                },
                {
                  "name": "services",
                  "kind": "ListServices",
                  "description": "List core services",
                  "parameters": { "scope": "critical" }
                }
              ]
            }
            """;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var plan = await engine.CreatePlanAsync(new CreatePlanRequest("Assess production fleet health"));

        plan.Status.Should().Be(PlanStatus.PendingApproval);
        plan.RiskLevel.Should().Be(PlanRiskLevel.Low);
        plan.PlanHash.Should().NotBeNullOrWhiteSpace();
        plan.Steps.Should().HaveCount(2);

        var fromStore = await engine.GetPlanAsync(plan.Id);
        fromStore.Should().NotBeNull();
        fromStore!.Status.Should().Be(PlanStatus.PendingApproval);
        fromStore.PlanHash.Should().Be(plan.PlanHash);
    }

    [Fact]
    public async Task CreatePlan_WithDeniedStep_IsRejectedByPolicy()
    {
        _llmClient.Content = """
            {
              "steps": [
                {
                  "name": "danger",
                  "kind": "RunScript",
                  "description": "Execute emergency script",
                  "parameters": { "script": "rm -rf /tmp/*" }
                }
              ]
            }
            """;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var plan = await engine.CreatePlanAsync(new CreatePlanRequest("Run emergency cleanup"));

        plan.Status.Should().Be(PlanStatus.Rejected);
        plan.RiskLevel.Should().Be(PlanRiskLevel.Critical);
        plan.RejectionReason.Should().Contain("denied kind");
    }

    [Fact]
    public async Task ApprovePlan_WithMatchingHash_TransitionsToApproved()
    {
        _llmClient.Content = """
            {
              "steps": [
                {
                  "name": "patch",
                  "kind": "ApplyPatch",
                  "description": "Apply approved patches",
                  "parameters": { "window": "now" }
                }
              ]
            }
            """;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var plan = await engine.CreatePlanAsync(new CreatePlanRequest("Patch staging nodes"));
        plan.Status.Should().Be(PlanStatus.PendingApproval);

        var approved = await engine.ApprovePlanAsync(plan.Id, new ApprovePlanRequest(plan.PlanHash));

        approved.Status.Should().Be(PlanStatus.Approved);
        approved.ApprovedUtc.Should().NotBeNull();

        var fromStore = await engine.GetPlanAsync(plan.Id);
        fromStore!.Status.Should().BeOneOf(PlanStatus.Approved, PlanStatus.Executing, PlanStatus.Completed);
        fromStore.ApprovedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ApprovePlan_WithHashMismatch_RejectsPlan()
    {
        _llmClient.Content = """
            {
              "steps": [
                {
                  "name": "metrics",
                  "kind": "GatherMetrics",
                  "description": "Collect baseline",
                  "parameters": { "tag": "prod" }
                }
              ]
            }
            """;

        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IAutomationEngine>();

        var plan = await engine.CreatePlanAsync(new CreatePlanRequest("Gather prod baseline"));

        var act = async () => await engine.ApprovePlanAsync(plan.Id, new ApprovePlanRequest("deadbeef"));
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*hash mismatch*");

        var fromStore = await engine.GetPlanAsync(plan.Id);
        fromStore.Should().NotBeNull();
        fromStore!.Status.Should().Be(PlanStatus.Rejected);
        fromStore.RejectionReason.Should().Contain("hash mismatch");
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

    private sealed class FakePlannerLlmClient : ILLMClient
    {
        public string Content { get; set; } = "{\"steps\":[]}";

        public Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new LLMGenerationResult(
                Success: true,
                Model: "fake-planner",
                Content: Content,
                PromptTokens: 10,
                CompletionTokens: 10,
                Latency: TimeSpan.FromMilliseconds(10)));
        }
    }
}
