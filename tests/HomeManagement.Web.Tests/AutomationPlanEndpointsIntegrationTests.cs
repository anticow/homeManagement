using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.AI.Abstractions.Contracts;
using HomeManagement.Automation;
using HomeManagement.Broker.Host.Hubs;
using HomeManagement.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Web.Tests;

public sealed class AutomationPlanEndpointsIntegrationTests : IAsyncLifetime, IDisposable
{
    private BrokerAutomationWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new BrokerAutomationWebApplicationFactory();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact]
    public async Task PostPlan_ThenGetById_ReturnsPersistedPlan()
    {
        _factory.LlmClient.PlannerContent = """
            {
              "steps": [
                {
                  "name": "metrics",
                  "kind": "GatherMetrics",
                  "description": "collect",
                  "parameters": { "tag": "prod", "maxTargets": "25" }
                }
              ]
            }
            """;

        var createResponse = await _client.PostAsJsonAsync("/api/automation/plans", new { objective = "collect prod metrics" });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var planId = createJson.RootElement.GetProperty("planId").GetGuid();

        var getResponse = await _client.GetAsync($"/api/automation/plans/{planId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getJson = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        getJson.RootElement.GetProperty("id").GetProperty("value").GetGuid().Should().Be(planId);
        ReadPlanStatus(getJson.RootElement.GetProperty("status")).Should().Be("PendingApproval");
    }

    [Fact]
    public async Task PostPlan_WithMalformedParameterPayload_Returns422AndDoesNotPersist()
    {
        _factory.LlmClient.PlannerContent = """
            {
              "steps": [
                {
                  "name": "metrics",
                  "kind": "GatherMetrics",
                  "description": "collect",
                  "parameters": { "tag": 123 }
                }
              ]
            }
            """;

        var response = await _client.PostAsJsonAsync("/api/automation/plans", new { objective = "collect prod metrics" });
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Contain("must be a string");
    }

    [Fact]
    public async Task ApprovePlan_DispatchesExecution_AndPlanTransitionsToCompleted()
    {
        _factory.LlmClient.PlannerContent = """
            {
              "steps": [
                {
                  "name": "metrics",
                  "kind": "GatherMetrics",
                  "description": "collect",
                  "parameters": { "tag": "prod", "maxTargets": "10" }
                },
                {
                  "name": "services",
                  "kind": "ListServices",
                  "description": "list",
                  "parameters": { "scope": "critical" }
                }
              ]
            }
            """;

        var createResponse = await _client.PostAsJsonAsync("/api/automation/plans", new { objective = "collect prod metrics" });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var createJson = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var planId = createJson.RootElement.GetProperty("planId").GetGuid();
        var planHash = createJson.RootElement.GetProperty("planHash").GetString();

        var approveResponse = await _client.PostAsJsonAsync(
            $"/api/automation/plans/{planId}/approve",
            new { expectedHash = planHash });

        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var timeoutAt = DateTime.UtcNow.AddSeconds(20);
        string? finalStatus = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            var getResponse = await _client.GetAsync($"/api/automation/plans/{planId}");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var getJson = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
            finalStatus = ReadPlanStatus(getJson.RootElement.GetProperty("status"));

            if (finalStatus is "Completed" or "Failed")
            {
                break;
            }

            await Task.Delay(150);
        }

        finalStatus.Should().Be("Completed", "approve-and-run should dispatch and complete a health-report-backed plan");
    }

    [Fact]
    public async Task PostServiceEnsureRunning_StartsRun_AndCompletesEndToEnd()
    {
        var postResponse = await _client.PostAsJsonAsync("/api/automation/runs/service-ensure-running", new
        {
            serviceName = "sshd",
            tag = "prod",
            maxTargets = 5,
            attemptRestart = true
        });

        postResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
        var runId = postJson.RootElement.GetProperty("runId").GetGuid();

        var timeoutAt = DateTime.UtcNow.AddSeconds(20);
        JsonElement runRoot = default;
        var finalState = string.Empty;

        while (DateTime.UtcNow < timeoutAt)
        {
            var runResponse = await _client.GetAsync($"/api/automation/runs/{runId}");
            runResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var runJson = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
            runRoot = runJson.RootElement.Clone();
            finalState = ReadRunState(runRoot.GetProperty("state"));

            if (finalState is "Completed" or "Failed")
            {
                break;
            }

            await Task.Delay(150);
        }

        finalState.Should().Be("Completed");
        runRoot.GetProperty("workflowName").GetString().Should().Be("service.ensure_running");
        runRoot.GetProperty("machineResults").GetArrayLength().Should().Be(1);
        runRoot.GetProperty("outputMarkdown").GetString().Should().Contain("Service Ensure Running Report");
    }

    [Fact]
    public async Task PostServiceEnsureRunning_WithEmptyServiceName_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/automation/runs/service-ensure-running", new
        {
            serviceName = "",
            tag = "prod",
            attemptRestart = true
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Contain("ServiceName must not be empty");
    }

    [Fact]
    public async Task PostPatchAll_StartsRun_AndCompletesEndToEnd()
    {
        var postResponse = await _client.PostAsJsonAsync("/api/automation/runs/patch-all", new
        {
            tag = "prod",
            maxTargets = 5,
            dryRun = false,
            allowReboot = false
        });

        postResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
        var runId = postJson.RootElement.GetProperty("runId").GetGuid();

        var timeoutAt = DateTime.UtcNow.AddSeconds(20);
        JsonElement runRoot = default;
        var finalState = string.Empty;

        while (DateTime.UtcNow < timeoutAt)
        {
            var runResponse = await _client.GetAsync($"/api/automation/runs/{runId}");
            runResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var runJson = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
            runRoot = runJson.RootElement.Clone();
            finalState = ReadRunState(runRoot.GetProperty("state"));

            if (finalState is "Completed" or "Failed")
            {
                break;
            }

            await Task.Delay(150);
        }

        finalState.Should().Be("Completed");
        runRoot.GetProperty("workflowName").GetString().Should().Be("fleet.patch_all");
        runRoot.GetProperty("machineResults").GetArrayLength().Should().Be(1);
        runRoot.GetProperty("outputMarkdown").GetString().Should().Contain("Fleet Patch All Report");
    }

    [Fact]
    public async Task PostPatchAll_WithNoTargets_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/automation/runs/patch-all", new
        {
            targetMachineIds = Array.Empty<Guid>(),
            tag = "",
            maxTargets = 1
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Contain("at least one target machine id");
    }

    [Fact]
    public async Task PostHaosHealthStatus_StartsRun_AndCompletesEndToEnd()
    {
        var postResponse = await _client.PostAsJsonAsync("/api/automation/runs/haos-health-status", new
        {
            instanceName = "haos-lab"
        });

        postResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
        var runId = postJson.RootElement.GetProperty("runId").GetGuid();

        var timeoutAt = DateTime.UtcNow.AddSeconds(20);
        JsonElement runRoot = default;
        var finalState = string.Empty;

        while (DateTime.UtcNow < timeoutAt)
        {
            var runResponse = await _client.GetAsync($"/api/automation/runs/{runId}");
            runResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var runJson = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
            runRoot = runJson.RootElement.Clone();
            finalState = ReadRunState(runRoot.GetProperty("state"));

            if (finalState is "Completed" or "Failed")
            {
                break;
            }

            await Task.Delay(150);
        }

        finalState.Should().Be("Completed");
        runRoot.GetProperty("workflowName").GetString().Should().Be("haos.health_status");
        runRoot.GetProperty("outputMarkdown").GetString().Should().Contain("HAOS Health Status Report");
    }

    [Fact]
    public async Task PostHaosEntitySnapshot_StartsRun_AndCompletesEndToEnd()
    {
        var postResponse = await _client.PostAsJsonAsync("/api/automation/runs/haos-entity-snapshot", new
        {
            instanceName = "haos-lab",
            domainFilter = "sensor",
            maxEntities = 50
        });

        postResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
        var runId = postJson.RootElement.GetProperty("runId").GetGuid();

        var timeoutAt = DateTime.UtcNow.AddSeconds(20);
        JsonElement runRoot = default;
        var finalState = string.Empty;

        while (DateTime.UtcNow < timeoutAt)
        {
            var runResponse = await _client.GetAsync($"/api/automation/runs/{runId}");
            runResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var runJson = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
            runRoot = runJson.RootElement.Clone();
            finalState = ReadRunState(runRoot.GetProperty("state"));

            if (finalState is "Completed" or "Failed")
            {
                break;
            }

            await Task.Delay(150);
        }

        finalState.Should().Be("Completed");
        runRoot.GetProperty("workflowName").GetString().Should().Be("haos.entity_snapshot");
        runRoot.GetProperty("outputMarkdown").GetString().Should().Contain("HAOS Entity Snapshot Report");
        runRoot.GetProperty("outputMarkdown").GetString().Should().Contain("sensor.temp_living");
    }

    [Fact]
    public async Task PostAnsibleHandoff_StartsRun_AndCompletesEndToEnd()
    {
        var postResponse = await _client.PostAsJsonAsync("/api/automation/runs/ansible-handoff", new
        {
            operation = "k3s.worker.add",
            targetScope = "worker-*",
            extraVarsJson = "{\"node\":\"worker-01\"}",
            dryRun = true,
            approveAndRun = true,
            approvedBy = "ops-admin",
            approvalReason = "validated",
            changeTicket = "CHG-2001"
        });

        postResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
        var runId = postJson.RootElement.GetProperty("runId").GetGuid();

        var timeoutAt = DateTime.UtcNow.AddSeconds(20);
        JsonElement runRoot = default;
        var finalState = string.Empty;

        while (DateTime.UtcNow < timeoutAt)
        {
            var runResponse = await _client.GetAsync($"/api/automation/runs/{runId}");
            runResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var runJson = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
            runRoot = runJson.RootElement.Clone();
            finalState = ReadRunState(runRoot.GetProperty("state"));

            if (finalState is "Completed" or "Failed")
            {
                break;
            }

            await Task.Delay(150);
        }

        finalState.Should().Be("Completed");
        runRoot.GetProperty("workflowName").GetString().Should().Be("ansible.handoff");
        runRoot.GetProperty("machineResults").GetArrayLength().Should().Be(1);
        runRoot.GetProperty("outputMarkdown").GetString().Should().Contain("Ansible Handoff Report");
    }

    [Fact]
    public async Task PostAnsibleHandoff_WithoutApprovalGuard_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/automation/runs/ansible-handoff", new
        {
            operation = "k3s.worker.add",
            dryRun = true,
            approveAndRun = false,
            approvedBy = "ops-admin",
            approvalReason = "validated"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Contain("ApproveAndRun must be true");
    }

    [Fact]
    public async Task PostAnsibleHandoff_WithExecutionTimeoutOutOfRange_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/automation/runs/ansible-handoff", new
        {
            operation = "k3s.worker.add",
            dryRun = true,
            executionTimeoutSeconds = 2,
            cancelOnTimeout = true,
            approveAndRun = true,
            approvedBy = "ops-admin",
            approvalReason = "validated"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Contain("ExecutionTimeoutSeconds must be between 5 and 3600");
    }

    [Fact]
    public async Task PostAnsibleHandoff_WithTimeoutButCancellationDisabled_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/automation/runs/ansible-handoff", new
        {
            operation = "k3s.worker.add",
            dryRun = true,
            executionTimeoutSeconds = 90,
            cancelOnTimeout = false,
            approveAndRun = true,
            approvedBy = "ops-admin",
            approvalReason = "validated"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Contain("CancelOnTimeout must be true");
    }

    [Theory]
    [InlineData("k3s.worker.add; ../scripts/pwn.sh")]
    [InlineData("k3s.worker.add && whoami")]
    [InlineData("k3s.worker.add | Invoke-Expression 'Get-ChildItem'")]
    [InlineData("k3s.worker.add\nproxmox.vm.provision")]
    [InlineData("k3s.worker.add --limit all")]
    public async Task PostAnsibleHandoff_WithAllowlistBypassPayload_ReturnsBadRequest(string operation)
    {
        var response = await _client.PostAsJsonAsync("/api/automation/runs/ansible-handoff", new
        {
            operation,
            dryRun = true,
            approveAndRun = true,
            approvedBy = "ops-admin",
            approvalReason = "validated"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Contain("allowlisted");
    }

    [Fact]
    public async Task DashboardSummary_ReturnsWorkflowAggregates_ForServiceAndPatchRuns()
    {
        await StartRunAndWaitAsync("/api/automation/runs/service-ensure-running", new
        {
            serviceName = "sshd",
            tag = "prod",
            maxTargets = 5,
            attemptRestart = true
        });

        await StartRunAndWaitAsync("/api/automation/runs/patch-all", new
        {
            tag = "prod",
            maxTargets = 5,
            dryRun = false,
            allowReboot = false
        });

        var response = await _client.GetAsync("/api/automation/dashboard/summary?hours=24");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var workflows = json.RootElement.GetProperty("workflows");
        workflows.GetArrayLength().Should().BeGreaterThan(0);

        workflows.EnumerateArray().Any(e => e.GetProperty("workflow").GetString() == "service.ensure_running").Should().BeTrue();
        workflows.EnumerateArray().Any(e => e.GetProperty("workflow").GetString() == "fleet.patch_all").Should().BeTrue();
    }

    [Fact]
    public async Task DashboardMachineOutcomes_ReturnsDrillDown_ForServiceAndPatchWorkflows()
    {
        await StartRunAndWaitAsync("/api/automation/runs/service-ensure-running", new
        {
            serviceName = "sshd",
            tag = "prod",
            maxTargets = 5,
            attemptRestart = true
        });

        await StartRunAndWaitAsync("/api/automation/runs/patch-all", new
        {
            tag = "prod",
            maxTargets = 5,
            dryRun = false,
            allowReboot = false
        });

        var serviceResponse = await _client.GetAsync("/api/automation/dashboard/machine-outcomes/service.ensure_running?hours=24&page=1&pageSize=50");
        serviceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var serviceJson = JsonDocument.Parse(await serviceResponse.Content.ReadAsStringAsync());
        serviceJson.RootElement.GetProperty("total").GetInt32().Should().BeGreaterThan(0);

        var patchResponse = await _client.GetAsync("/api/automation/dashboard/machine-outcomes/fleet.patch_all?hours=24&page=1&pageSize=50");
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var patchJson = JsonDocument.Parse(await patchResponse.Content.ReadAsStringAsync());
        patchJson.RootElement.GetProperty("total").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DashboardStepFailures_ReturnsEmptyOrFailureRows()
    {
        await StartRunAndWaitAsync("/api/automation/runs/haos-health-status", new
        {
            instanceName = "haos-lab"
        });

        var response = await _client.GetAsync("/api/automation/dashboard/step-failures?hours=24");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var stepFailures = json.RootElement.GetProperty("stepFailures");
        stepFailures.ValueKind.Should().Be(JsonValueKind.Array);
    }

    private static string ReadPlanStatus(JsonElement statusElement)
    {
        if (statusElement.ValueKind == JsonValueKind.String)
        {
            return statusElement.GetString() ?? string.Empty;
        }

        if (statusElement.ValueKind == JsonValueKind.Number && statusElement.TryGetInt32(out var enumValue))
        {
            return Enum.IsDefined(typeof(PlanStatus), enumValue)
                ? ((PlanStatus)enumValue).ToString()
                : enumValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return statusElement.ToString();
    }

    private static string ReadRunState(JsonElement stateElement)
    {
        if (stateElement.ValueKind == JsonValueKind.String)
        {
            return stateElement.GetString() ?? string.Empty;
        }

        if (stateElement.ValueKind == JsonValueKind.Number && stateElement.TryGetInt32(out var enumValue))
        {
            return Enum.IsDefined(typeof(AutomationRunStateKind), enumValue)
                ? ((AutomationRunStateKind)enumValue).ToString()
                : enumValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return stateElement.ToString();
    }

    private async Task<JsonElement> StartRunAndWaitAsync(string endpoint, object payload)
    {
        var postResponse = await _client.PostAsJsonAsync(endpoint, payload);
        postResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var postJson = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
        var runId = postJson.RootElement.GetProperty("runId").GetGuid();

        var timeoutAt = DateTime.UtcNow.AddSeconds(20);
        JsonElement runRoot = default;
        var finalState = string.Empty;

        while (DateTime.UtcNow < timeoutAt)
        {
            var runResponse = await _client.GetAsync($"/api/automation/runs/{runId}");
            runResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var runJson = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
            runRoot = runJson.RootElement.Clone();
            finalState = ReadRunState(runRoot.GetProperty("state"));

            if (finalState is "Completed" or "Failed")
            {
                break;
            }

            await Task.Delay(150);
        }

        finalState.Should().Be("Completed");
        return runRoot;
    }

    private sealed class BrokerAutomationWebApplicationFactory : WebApplicationFactory<EventHub>
    {
        private readonly string _databasePath;
        private readonly string _dataDirectory;

        public BrokerAutomationWebApplicationFactory()
        {
            _dataDirectory = Path.Combine(Path.GetTempPath(), $"hm-broker-automation-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dataDirectory);
            _databasePath = Path.Combine(_dataDirectory, "broker-automation-tests.db");
            LlmClient = new FakePlanLlmClient();
        }

        public FakePlanLlmClient LlmClient { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:HomeManagement"] = $"Data Source={_databasePath}",
                    ["DataDirectory"] = _dataDirectory,
                    ["Auth:JwtSigningKey"] = "integration-test-signing-key-that-is-long-enough-for-hmac-sha256!",
                    ["Auth:Issuer"] = "test-issuer",
                    ["Auth:Audience"] = "test-audience",
                    ["Auth:BootstrapAdmin:Enabled"] = "true",
                    ["Auth:BootstrapAdmin:Username"] = "admin",
                    ["Auth:BootstrapAdmin:Password"] = "HomeManagement_TestAdmin1!",
                    ["Auth:BootstrapAdmin:DisplayName"] = "Integration Admin",
                    ["Auth:BootstrapAdmin:Email"] = "admin@test.local",
                    ["AI:Enabled"] = "true",
                    ["AI:Provider"] = "Ollama",
                    ["AI:Ollama:BaseUrl"] = "http://localhost:11434",
                    ["AI:Ollama:Model"] = "fake",
                    ["Automation:Enabled"] = "true",
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<HomeManagementDbContext>>();
                services.RemoveAll<HomeManagementDbContext>();
                services.RemoveAll<ILLMClient>();
                services.RemoveAll<IInventoryService>();
                services.RemoveAll<IServiceController>();
                services.RemoveAll<IPatchService>();
                services.RemoveAll<IHaosAdapter>();
                services.RemoveAll<IAuditLogger>();

                services.AddDbContext<HomeManagementDbContext>(options =>
                    options.UseSqlite($"Data Source={_databasePath}"));

                services.AddSingleton<ILLMClient>(LlmClient);
                services.AddScoped<IInventoryService, FakeInventoryService>();
                services.AddScoped<IServiceController, FakeServiceController>();
                services.AddScoped<IPatchService, FakePatchService>();
                services.AddScoped<IHaosAdapter, FakeHaosAdapter>();
                services.AddScoped<IAuditLogger, FakeAuditLogger>();

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    _ => { });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();

            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(_dataDirectory))
                    {
                        Directory.Delete(_dataDirectory, recursive: true);
                    }

                    return;
                }
                catch (IOException)
                {
                    if (attempt == 4)
                    {
                        break;
                    }

                    await Task.Delay(100);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt == 4)
                    {
                        break;
                    }

                    await Task.Delay(100);
                }
            }
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-user"),
                new Claim(ClaimTypes.Name, "test-user"),
                new Claim(ClaimTypes.Role, "Admin")
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class FakePlanLlmClient : ILLMClient
    {
        private readonly object _sync = new();
        private string _plannerContent = "{\"steps\":[]}";

        public string PlannerContent
        {
            get { lock (_sync) { return _plannerContent; } }
            set { lock (_sync) { _plannerContent = value; } }
        }

        public Task<LLMGenerationResult> GenerateAsync(LLMGenerationRequest request, CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt)
                && request.SystemPrompt.Contains("structured workflow plan", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new LLMGenerationResult(
                    Success: true,
                    Model: "fake-planner",
                    Content: PlannerContent,
                    PromptTokens: 40,
                    CompletionTokens: 80,
                    Latency: TimeSpan.FromMilliseconds(8)));
            }

            return Task.FromResult(new LLMGenerationResult(
                Success: true,
                Model: "fake-summary",
                Content: "All systems healthy. No urgent action.",
                PromptTokens: 20,
                CompletionTokens: 16,
                Latency: TimeSpan.FromMilliseconds(8)));
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
        public Task<ServiceInfo> GetStatusAsync(MachineTarget target, ServiceName serviceName, CancellationToken ct = default)
        {
            return Task.FromResult(new ServiceInfo(
                serviceName,
                serviceName.Value,
                ServiceState.Stopped,
                ServiceStartupType.Automatic,
                null,
                TimeSpan.FromHours(1),
                []));
        }

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

        public Task<ServiceActionResult> ControlAsync(MachineTarget target, ServiceName serviceName, ServiceAction action, CancellationToken ct = default)
        {
            return Task.FromResult(new ServiceActionResult(
                target.MachineId,
                serviceName,
                action,
                Success: true,
                ResultingState: ServiceState.Running,
                ErrorMessage: null,
                Duration: TimeSpan.FromMilliseconds(15)));
        }
        public Task<IReadOnlyList<ServiceActionResult>> BulkControlAsync(IReadOnlyList<MachineTarget> targets, ServiceName serviceName, ServiceAction action, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakePatchService : IPatchService
    {
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
            {
                yield return item;
            }
        }

        public Task<PatchResult> ApplyAsync(MachineTarget target, IReadOnlyList<PatchInfo> patches, PatchOptions options, CancellationToken ct = default)
        {
            return Task.FromResult(new PatchResult(
                target.MachineId,
                Successful: patches.Count,
                Failed: 0,
                Outcomes: patches.Select(p => new PatchOutcome(p.PatchId, PatchInstallState.Installed, null)).ToList(),
                RebootRequired: false,
                Duration: TimeSpan.FromMilliseconds(20)));
        }

        public Task<PatchResult> VerifyAsync(MachineTarget target, IReadOnlyList<string> patchIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(Guid machineId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PatchHistoryEntry>>([]);
        public Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(MachineTarget target, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InstalledPatch>>([]);
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
                Metadata: new Dictionary<string, string>
                {
                    ["mode"] = "read-only"
                }));
        }

        public Task<IReadOnlyList<HaosEntityState>> GetEntitiesAsync(string? domainFilter = null, int maxEntities = 250, string? instanceName = null, CancellationToken ct = default)
        {
            IReadOnlyList<HaosEntityState> entities =
            [
                new HaosEntityState("sensor.temp_living", "22.1", DateTime.UtcNow, new Dictionary<string, string> { ["unit"] = "C" }),
                new HaosEntityState("sensor.humidity_living", "48", DateTime.UtcNow, new Dictionary<string, string> { ["unit"] = "%" }),
                new HaosEntityState("switch.pool_pump", "off", DateTime.UtcNow, new Dictionary<string, string>())
            ];

            var filtered = string.IsNullOrWhiteSpace(domainFilter)
                ? entities
                : entities.Where(e => e.EntityId.StartsWith(domainFilter + ".", StringComparison.OrdinalIgnoreCase)).ToList();

            return Task.FromResult<IReadOnlyList<HaosEntityState>>(filtered.Take(maxEntities).ToList());
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
