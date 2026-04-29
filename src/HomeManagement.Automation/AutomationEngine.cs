using System.Text.Json;
using System.Diagnostics;
using HomeManagement.Abstractions;
using HomeManagement.AI.Abstractions.Contracts;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Automation;

internal sealed class AutomationEngine : IAutomationEngine
{
    private const string HealthReportWorkflowName = "fleet.health_report";
    private const string EnsureServiceRunningWorkflowName = "service.ensure_running";
    private const string PatchAllWorkflowName = "fleet.patch_all";
    private const string HaosHealthStatusWorkflowName = "haos.health_status";
    private const string HaosEntitySnapshotWorkflowName = "haos.entity_snapshot";
    private const string AnsibleHandoffWorkflowName = "ansible.handoff";

    private readonly IOptionsMonitor<AutomationOptions> _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AutomationEngine> _logger;

    public AutomationEngine(
        IOptionsMonitor<AutomationOptions> options,
        IServiceProvider serviceProvider,
        ILogger<AutomationEngine> logger)
    {
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_options.CurrentValue.Enabled);
    }

    public Task<AutomationRunId> StartHealthReportAsync(HealthReportRunRequest request, CancellationToken ct = default) =>
        StartWorkflowAsync(HealthReportWorkflowName, request,
            async (scope, runId, req, correlationId, executionCt) =>
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
                var inventoryService = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                var serviceController = scope.ServiceProvider.GetRequiredService<IServiceController>();
                var llmClient = scope.ServiceProvider.GetRequiredService<ILLMClient>();
                await ExecuteHealthReportAsync(runId, req, correlationId, repository, inventoryService, serviceController, llmClient, executionCt);
            }, ct);

    public async Task<AutomationRunId> StartEnsureServiceRunningAsync(EnsureServiceRunningRunRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ServiceName))
            throw new ArgumentException("ServiceName must not be empty.", nameof(request));

        return await StartWorkflowAsync(EnsureServiceRunningWorkflowName, request,
            async (scope, runId, req, correlationId, executionCt) =>
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
                var inventoryService = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                var serviceController = scope.ServiceProvider.GetRequiredService<IServiceController>();
                await ExecuteEnsureServiceRunningAsync(runId, req, correlationId, repository, inventoryService, serviceController, executionCt);
            }, ct);
    }

    public Task<AutomationRunId> StartPatchAllAsync(PatchAllRunRequest request, CancellationToken ct = default) =>
        StartWorkflowAsync(PatchAllWorkflowName, request,
            async (scope, runId, req, correlationId, executionCt) =>
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
                var inventoryService = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                var patchService = scope.ServiceProvider.GetRequiredService<IPatchService>();
                await ExecutePatchAllAsync(runId, req, correlationId, repository, inventoryService, patchService, executionCt);
            }, ct);

    public Task<AutomationRunId> StartHaosHealthStatusAsync(HaosHealthStatusRunRequest request, CancellationToken ct = default) =>
        StartWorkflowAsync(HaosHealthStatusWorkflowName, request,
            async (scope, runId, req, correlationId, executionCt) =>
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
                var haosAdapter = scope.ServiceProvider.GetRequiredService<IHaosAdapter>();
                await ExecuteHaosHealthStatusAsync(runId, req, correlationId, repository, haosAdapter, executionCt);
            }, ct);

    public Task<AutomationRunId> StartHaosEntitySnapshotAsync(HaosEntitySnapshotRunRequest request, CancellationToken ct = default) =>
        StartWorkflowAsync(HaosEntitySnapshotWorkflowName, request,
            async (scope, runId, req, correlationId, executionCt) =>
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
                var haosAdapter = scope.ServiceProvider.GetRequiredService<IHaosAdapter>();
                await ExecuteHaosEntitySnapshotAsync(runId, req, correlationId, repository, haosAdapter, executionCt);
            }, ct);

    public async Task<AutomationRunId> StartAnsibleHandoffAsync(AnsibleHandoffRunRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Operation))
            throw new ArgumentException("Operation must not be empty.", nameof(request));

        return await StartWorkflowAsync(AnsibleHandoffWorkflowName, request,
            async (scope, runId, req, correlationId, executionCt) =>
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
                var ansibleHandoffService = scope.ServiceProvider.GetRequiredService<IAnsibleHandoffService>();
                await ExecuteAnsibleHandoffAsync(runId, req, correlationId, repository, ansibleHandoffService, executionCt);
            }, ct);
    }

    /// <summary>
    /// Common scaffold for all workflow dispatches: guards enabled state, persists the run record,
    /// fires the execute delegate in a background task, and handles failures uniformly.
    /// </summary>
    private async Task<AutomationRunId> StartWorkflowAsync<TRequest>(
        string workflowName,
        TRequest request,
        Func<IServiceScope, AutomationRunId, TRequest, string, CancellationToken, Task> executeAsync,
        CancellationToken ct)
    {
        if (!await IsEnabledAsync(ct))
            throw new InvalidOperationException("Automation is currently disabled.");

        var runId = AutomationRunId.New();
        var correlationId = Guid.NewGuid().ToString("N");
        AutomationTelemetry.RecordRunStarted(workflowName);

        using (var scope = _serviceProvider.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
            var requestJson = JsonSerializer.Serialize(request);
            await repository.CreateRunAsync(runId.Value, workflowName, requestJson, correlationId, ct);
            await RecordAuditEventAsync(
                correlationId,
                AuditAction.JobSubmitted,
                AuditOutcome.Success,
                $"Automation run queued for workflow '{workflowName}'.",
                runId,
                workflowName,
                CancellationToken.None);
        }

        _ = Task.Run(async () =>
        {
            var executionCt = CancellationToken.None;
            var runStopwatch = Stopwatch.StartNew();

            try
            {
                using var scope = _serviceProvider.CreateScope();
                await executeAsync(scope, runId, request, correlationId, executionCt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automation run {RunId} failed", runId.Value);
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
                await repository.UpdateRunFailedAsync(runId.Value, ex.Message, executionCt);
                await RecordAuditEventAsync(
                    correlationId,
                    AuditAction.JobFailed,
                    AuditOutcome.Failure,
                    $"Automation run failed for workflow '{workflowName}'.",
                    runId,
                    workflowName,
                    executionCt,
                    ex.Message);
                AutomationTelemetry.RecordRunCompleted(workflowName, success: false, runStopwatch.Elapsed.TotalMilliseconds);
            }
        }, CancellationToken.None);

        return runId;
    }
    public async Task<AutomationRun?> GetRunAsync(AutomationRunId runId, CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
        return await repository.GetRunAsync(runId.Value, ct);
    }

    public async Task<IReadOnlyList<AutomationRunSummary>> ListRunsAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var normalizedPage = Math.Max(1, page);
        var normalizedPageSize = Math.Clamp(pageSize, 1, 200);

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
        return await repository.ListRunsAsync(normalizedPage, normalizedPageSize, ct);
    }

    private async Task ExecuteHealthReportAsync(
        AutomationRunId runId,
        HealthReportRunRequest request,
        string correlationId,
        IAutomationRunRepository repository,
        IInventoryService inventoryService,
        IServiceController serviceController,
        ILLMClient llmClient,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await repository.UpdateRunStateAsync(runId.Value, AutomationRunStateKind.Running.ToString(), ct);

        var targets = await ResolveTargetsAsync(runId, request, repository, inventoryService, HealthReportWorkflowName, ct);
        await repository.UpdateTotalMachinesAsync(runId.Value, targets.Count, ct);

        var metrics = await GatherMetricsAsync(runId, targets, repository, inventoryService, ct);
        var serviceCounts = await GatherServiceStatusAsync(runId, targets, repository, serviceController, ct);

        var completedCount = 0;
        var failedCount = 0;

        foreach (var machine in targets)
        {
            metrics.TryGetValue(machine.Id, out var metricsMachine);
            serviceCounts.TryGetValue(machine.Id, out var runningServices);

            var success = metricsMachine is not null;

            var errorMessage = success ? null : "Unable to gather machine metadata.";
            var resultData = success ? new
            {
                cpuCores = metricsMachine?.Hardware?.CpuCores ?? 0,
                ramBytes = metricsMachine?.Hardware?.RamBytes,
                architecture = metricsMachine?.Hardware?.Architecture,
                runningServices = runningServices
            } : null;

            var resultDataJson = success ? JsonSerializer.Serialize(resultData) : null;
            await repository.AddMachineResultAsync(
                runId.Value,
                machine.Id,
                machine.Hostname.Value,
                success,
                errorMessage,
                resultDataJson,
                ct);
            AutomationTelemetry.RecordMachineOutcome(HealthReportWorkflowName, success);

            if (success)
                completedCount++;
            else
                failedCount++;
        }

        // Generate output
        var output = new
        {
            workflow = HealthReportWorkflowName,
            generatedUtc = DateTime.UtcNow,
            totalTargets = targets.Count,
            successCount = completedCount,
            failedCount = failedCount
        };

        var outputJson = JsonSerializer.Serialize(output);
        var llmSummary = await GenerateSummaryAsync(runId, outputJson, repository, llmClient, ct);

        var markdownLines = new List<string>
        {
            "# Fleet Health Report",
            string.Empty,
            $"- Total targets: {targets.Count}",
            $"- Successful: {completedCount}",
            $"- Failed: {failedCount}",
            string.Empty,
            "## Machines"
        };

        if (!string.IsNullOrWhiteSpace(llmSummary))
        {
            markdownLines.Add("## AI Summary");
            markdownLines.Add(llmSummary);
            markdownLines.Add(string.Empty);
        }

        // Fetch final results to build markdown
        var finalRun = await repository.GetRunAsync(runId.Value, ct);
        if (finalRun?.MachineResults is not null)
        {
            foreach (var result in finalRun.MachineResults.OrderBy(m => m.MachineName, StringComparer.OrdinalIgnoreCase))
            {
                var marker = result.Success ? "ok" : "fail";
                markdownLines.Add($"- {marker} {result.MachineName}: success={result.Success}");
            }
        }

        var outputMarkdown = string.Join(Environment.NewLine, markdownLines);

        await repository.UpdateRunCompletedAsync(
            runId.Value,
            AutomationRunStateKind.Completed.ToString(),
            completedCount,
            failedCount,
            outputJson,
            outputMarkdown,
            ct);
        var outcome = failedCount > 0 ? AuditOutcome.PartialSuccess : AuditOutcome.Success;
        await RecordAuditEventAsync(
            correlationId,
            AuditAction.JobCompleted,
            outcome,
            $"Automation run completed for workflow '{HealthReportWorkflowName}'.",
            runId,
            HealthReportWorkflowName,
            ct);
        AutomationTelemetry.RecordRunCompleted(HealthReportWorkflowName, success: failedCount == 0, stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task ExecuteEnsureServiceRunningAsync(
        AutomationRunId runId,
        EnsureServiceRunningRunRequest request,
        string correlationId,
        IAutomationRunRepository repository,
        IInventoryService inventoryService,
        IServiceController serviceController,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await repository.UpdateRunStateAsync(runId.Value, AutomationRunStateKind.Running.ToString(), ct);

        var serviceName = HomeManagement.Abstractions.Validation.ServiceName.Create(request.ServiceName);

        var targets = await ResolveTargetsAsync(
            runId,
            new HealthReportRunRequest(request.TargetMachineIds, request.Tag, request.MaxTargets),
            repository,
            inventoryService,
            EnsureServiceRunningWorkflowName,
            ct);
        await repository.UpdateTotalMachinesAsync(runId.Value, targets.Count, ct);

        var stepId = Guid.NewGuid();
        await repository.AddStepAsync(runId.Value, stepId, "ensure_service_running", ct);
        await repository.UpdateStepStateAsync(stepId, AutomationStepState.Running.ToString(), ct);

        var completedCount = 0;
        var failedCount = 0;

        foreach (var machine in targets)
        {
            var success = false;
            string? errorMessage = null;
            var beforeState = ServiceState.Unknown;
            var afterState = ServiceState.Unknown;
            var actionTaken = "none";

            try
            {
                var target = ToMachineTarget(machine);
                var status = await serviceController.GetStatusAsync(target, serviceName, ct);
                beforeState = status.State;

                if (status.State == ServiceState.Running)
                {
                    success = true;
                    afterState = ServiceState.Running;
                    actionTaken = "none";
                }
                else if (request.AttemptRestart)
                {
                    var action = status.State == ServiceState.Stopped
                        ? ServiceAction.Start
                        : ServiceAction.Restart;
                    actionTaken = action.ToString();

                    var result = await serviceController.ControlAsync(target, serviceName, action, ct);
                    afterState = result.ResultingState;
                    success = result.Success && result.ResultingState == ServiceState.Running;
                    if (!success)
                    {
                        errorMessage = result.ErrorMessage ?? "Service did not reach Running state after control action.";
                    }
                }
                else
                {
                    afterState = status.State;
                    errorMessage = $"Service '{serviceName.Value}' is {status.State} and AttemptRestart=false.";
                }
            }
            catch (Exception ex)
            {
                success = false;
                errorMessage = ex.Message;
            }

            var resultDataJson = JsonSerializer.Serialize(new
            {
                service = serviceName.Value,
                beforeState = beforeState.ToString(),
                afterState = afterState.ToString(),
                action = actionTaken
            });

            await repository.AddMachineResultAsync(
                runId.Value,
                machine.Id,
                machine.Hostname.Value,
                success,
                errorMessage,
                resultDataJson,
                ct);
            AutomationTelemetry.RecordMachineOutcome(EnsureServiceRunningWorkflowName, success);

            if (success)
                completedCount++;
            else
                failedCount++;
        }

        await repository.UpdateStepCompletedAsync(stepId, ct);

        var output = new
        {
            workflow = EnsureServiceRunningWorkflowName,
            service = serviceName.Value,
            generatedUtc = DateTime.UtcNow,
            totalTargets = targets.Count,
            successCount = completedCount,
            failedCount,
            attemptRestart = request.AttemptRestart
        };

        var outputJson = JsonSerializer.Serialize(output);
        var markdownLines = new List<string>
        {
            "# Service Ensure Running Report",
            string.Empty,
            $"- Service: {serviceName.Value}",
            $"- Total targets: {targets.Count}",
            $"- Successful: {completedCount}",
            $"- Failed: {failedCount}",
            string.Empty,
            "## Machines"
        };

        var finalRun = await repository.GetRunAsync(runId.Value, ct);
        if (finalRun?.MachineResults is not null)
        {
            foreach (var result in finalRun.MachineResults.OrderBy(m => m.MachineName, StringComparer.OrdinalIgnoreCase))
            {
                var marker = result.Success ? "ok" : "fail";
                markdownLines.Add($"- {marker} {result.MachineName}: success={result.Success}");
            }
        }

        var outputMarkdown = string.Join(Environment.NewLine, markdownLines);

        await repository.UpdateRunCompletedAsync(
            runId.Value,
            AutomationRunStateKind.Completed.ToString(),
            completedCount,
            failedCount,
            outputJson,
            outputMarkdown,
            ct);

        var outcome = failedCount > 0 ? AuditOutcome.PartialSuccess : AuditOutcome.Success;
        await RecordAuditEventAsync(
            correlationId,
            AuditAction.JobCompleted,
            outcome,
            $"Automation run completed for workflow '{EnsureServiceRunningWorkflowName}'.",
            runId,
            EnsureServiceRunningWorkflowName,
            ct);
        AutomationTelemetry.RecordRunCompleted(EnsureServiceRunningWorkflowName, success: failedCount == 0, stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task ExecutePatchAllAsync(
        AutomationRunId runId,
        PatchAllRunRequest request,
        string correlationId,
        IAutomationRunRepository repository,
        IInventoryService inventoryService,
        IPatchService patchService,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await repository.UpdateRunStateAsync(runId.Value, AutomationRunStateKind.Running.ToString(), ct);

        var targets = await ResolveTargetsAsync(
            runId,
            new HealthReportRunRequest(request.TargetMachineIds, request.Tag, request.MaxTargets),
            repository,
            inventoryService,
            PatchAllWorkflowName,
            ct);
        await repository.UpdateTotalMachinesAsync(runId.Value, targets.Count, ct);

        var stepId = Guid.NewGuid();
        await repository.AddStepAsync(runId.Value, stepId, "patch_all", ct);
        await repository.UpdateStepStateAsync(stepId, AutomationStepState.Running.ToString(), ct);

        var completedCount = 0;
        var failedCount = 0;

        foreach (var machine in targets)
        {
            var success = false;
            string? errorMessage = null;
            var detectedCount = 0;
            var appliedSuccessCount = 0;
            var appliedFailedCount = 0;
            var rebootRequired = false;

            try
            {
                var target = ToMachineTarget(machine);
                var patches = await patchService.DetectAsync(target, ct);
                detectedCount = patches.Count;

                if (detectedCount == 0 || request.DryRun)
                {
                    success = true;
                }
                else
                {
                    var options = new PatchOptions(AllowReboot: request.AllowReboot, DryRun: false);
                    var result = await patchService.ApplyAsync(target, patches, options, ct);
                    appliedSuccessCount = result.Successful;
                    appliedFailedCount = result.Failed;
                    rebootRequired = result.RebootRequired;
                    success = result.Failed == 0;
                    if (!success)
                    {
                        errorMessage = $"Patch apply reported {result.Failed} failed operations.";
                    }
                }
            }
            catch (Exception ex)
            {
                success = false;
                errorMessage = ex.Message;
            }

            var resultDataJson = JsonSerializer.Serialize(new
            {
                detectedPatches = detectedCount,
                appliedSuccessful = appliedSuccessCount,
                appliedFailed = appliedFailedCount,
                rebootRequired,
                dryRun = request.DryRun
            });

            await repository.AddMachineResultAsync(
                runId.Value,
                machine.Id,
                machine.Hostname.Value,
                success,
                errorMessage,
                resultDataJson,
                ct);
            AutomationTelemetry.RecordMachineOutcome(PatchAllWorkflowName, success);

            if (success)
                completedCount++;
            else
                failedCount++;
        }

        await repository.UpdateStepCompletedAsync(stepId, ct);

        var output = new
        {
            workflow = PatchAllWorkflowName,
            generatedUtc = DateTime.UtcNow,
            totalTargets = targets.Count,
            successCount = completedCount,
            failedCount,
            dryRun = request.DryRun,
            allowReboot = request.AllowReboot
        };

        var outputJson = JsonSerializer.Serialize(output);
        var markdownLines = new List<string>
        {
            "# Fleet Patch All Report",
            string.Empty,
            $"- Total targets: {targets.Count}",
            $"- Successful: {completedCount}",
            $"- Failed: {failedCount}",
            $"- DryRun: {request.DryRun}",
            string.Empty,
            "## Machines"
        };

        var finalRun = await repository.GetRunAsync(runId.Value, ct);
        if (finalRun?.MachineResults is not null)
        {
            foreach (var result in finalRun.MachineResults.OrderBy(m => m.MachineName, StringComparer.OrdinalIgnoreCase))
            {
                var marker = result.Success ? "ok" : "fail";
                markdownLines.Add($"- {marker} {result.MachineName}: success={result.Success}");
            }
        }

        var outputMarkdown = string.Join(Environment.NewLine, markdownLines);

        await repository.UpdateRunCompletedAsync(
            runId.Value,
            AutomationRunStateKind.Completed.ToString(),
            completedCount,
            failedCount,
            outputJson,
            outputMarkdown,
            ct);

        var outcome = failedCount > 0 ? AuditOutcome.PartialSuccess : AuditOutcome.Success;
        await RecordAuditEventAsync(
            correlationId,
            AuditAction.JobCompleted,
            outcome,
            $"Automation run completed for workflow '{PatchAllWorkflowName}'.",
            runId,
            PatchAllWorkflowName,
            ct);
        AutomationTelemetry.RecordRunCompleted(PatchAllWorkflowName, success: failedCount == 0, stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task ExecuteHaosHealthStatusAsync(
        AutomationRunId runId,
        HaosHealthStatusRunRequest request,
        string correlationId,
        IAutomationRunRepository repository,
        IHaosAdapter haosAdapter,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await repository.UpdateRunStateAsync(runId.Value, AutomationRunStateKind.Running.ToString(), ct);

        var stepId = Guid.NewGuid();
        await repository.AddStepAsync(runId.Value, stepId, "haos_supervisor_status", ct);
        await repository.UpdateStepStateAsync(stepId, AutomationStepState.Running.ToString(), ct);

        var status = await haosAdapter.GetSupervisorStatusAsync(request.InstanceName, ct);

        await repository.AddMachineResultAsync(
            runId.Value,
            Guid.NewGuid(),
            status.InstanceName,
            success: !string.Equals(status.Health, "Unhealthy", StringComparison.OrdinalIgnoreCase),
            errorMessage: null,
            resultDataJson: JsonSerializer.Serialize(status),
            ct);
        AutomationTelemetry.RecordMachineOutcome(
            HaosHealthStatusWorkflowName,
            success: !string.Equals(status.Health, "Unhealthy", StringComparison.OrdinalIgnoreCase));

        await repository.UpdateStepCompletedAsync(stepId, ct);
        await repository.UpdateTotalMachinesAsync(runId.Value, 1, ct);

        var outputJson = JsonSerializer.Serialize(new
        {
            workflow = HaosHealthStatusWorkflowName,
            generatedUtc = DateTime.UtcNow,
            supervisor = status
        });

        var outputMarkdown = string.Join(Environment.NewLine,
        [
            "# HAOS Health Status Report",
            string.Empty,
            $"- Instance: {status.InstanceName}",
            $"- Health: {status.Health}",
            $"- Version: {status.Version}",
            $"- RetrievedUtc: {status.RetrievedUtc:O}"
        ]);

        await repository.UpdateRunCompletedAsync(
            runId.Value,
            AutomationRunStateKind.Completed.ToString(),
            completedMachines: 1,
            failedMachines: 0,
            outputJson,
            outputMarkdown,
            ct);

        await RecordAuditEventAsync(
            correlationId,
            AuditAction.JobCompleted,
            AuditOutcome.Success,
            $"Automation run completed for workflow '{HaosHealthStatusWorkflowName}'.",
            runId,
            HaosHealthStatusWorkflowName,
            ct);
        AutomationTelemetry.RecordRunCompleted(HaosHealthStatusWorkflowName, success: true, stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task ExecuteHaosEntitySnapshotAsync(
        AutomationRunId runId,
        HaosEntitySnapshotRunRequest request,
        string correlationId,
        IAutomationRunRepository repository,
        IHaosAdapter haosAdapter,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await repository.UpdateRunStateAsync(runId.Value, AutomationRunStateKind.Running.ToString(), ct);

        var statusStepId = Guid.NewGuid();
        await repository.AddStepAsync(runId.Value, statusStepId, "haos_supervisor_status", ct);
        await repository.UpdateStepStateAsync(statusStepId, AutomationStepState.Running.ToString(), ct);
        var status = await haosAdapter.GetSupervisorStatusAsync(request.InstanceName, ct);
        await repository.UpdateStepCompletedAsync(statusStepId, ct);

        var snapshotStepId = Guid.NewGuid();
        await repository.AddStepAsync(runId.Value, snapshotStepId, "haos_entity_snapshot", ct);
        await repository.UpdateStepStateAsync(snapshotStepId, AutomationStepState.Running.ToString(), ct);

        var entities = await haosAdapter.GetEntitiesAsync(
            request.DomainFilter,
            Math.Clamp(request.MaxEntities, 1, 2000),
            request.InstanceName,
            ct);

        await repository.AddMachineResultAsync(
            runId.Value,
            Guid.NewGuid(),
            status.InstanceName,
            success: true,
            errorMessage: null,
            resultDataJson: JsonSerializer.Serialize(new { entityCount = entities.Count, domain = request.DomainFilter }),
            ct);
        AutomationTelemetry.RecordMachineOutcome(HaosEntitySnapshotWorkflowName, success: true);

        await repository.UpdateStepCompletedAsync(snapshotStepId, ct);
        await repository.UpdateTotalMachinesAsync(runId.Value, 1, ct);

        var outputJson = JsonSerializer.Serialize(new
        {
            workflow = HaosEntitySnapshotWorkflowName,
            generatedUtc = DateTime.UtcNow,
            supervisor = status,
            entityCount = entities.Count,
            entities
        });

        var preview = entities.Take(10).Select(e => $"- {e.EntityId}: {e.State}").ToList();
        var markdownLines = new List<string>
        {
            "# HAOS Entity Snapshot Report",
            string.Empty,
            $"- Instance: {status.InstanceName}",
            $"- Health: {status.Health}",
            $"- DomainFilter: {(string.IsNullOrWhiteSpace(request.DomainFilter) ? "none" : request.DomainFilter)}",
            $"- EntitiesCaptured: {entities.Count}",
            string.Empty,
            "## Entity Preview"
        };
        markdownLines.AddRange(preview.Count > 0 ? preview : ["- No entities returned"]);

        await repository.UpdateRunCompletedAsync(
            runId.Value,
            AutomationRunStateKind.Completed.ToString(),
            completedMachines: 1,
            failedMachines: 0,
            outputJson,
            string.Join(Environment.NewLine, markdownLines),
            ct);

        await RecordAuditEventAsync(
            correlationId,
            AuditAction.JobCompleted,
            AuditOutcome.Success,
            $"Automation run completed for workflow '{HaosEntitySnapshotWorkflowName}'.",
            runId,
            HaosEntitySnapshotWorkflowName,
            ct);
        AutomationTelemetry.RecordRunCompleted(HaosEntitySnapshotWorkflowName, success: true, stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task ExecuteAnsibleHandoffAsync(
        AutomationRunId runId,
        AnsibleHandoffRunRequest request,
        string correlationId,
        IAutomationRunRepository repository,
        IAnsibleHandoffService ansibleHandoffService,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await repository.UpdateRunStateAsync(runId.Value, AutomationRunStateKind.Running.ToString(), ct);

        var stepId = Guid.NewGuid();
        await repository.AddStepAsync(runId.Value, stepId, "ansible_handoff", ct);
        await repository.UpdateStepStateAsync(stepId, AutomationStepState.Running.ToString(), ct);

        var result = await ansibleHandoffService.ExecuteAsync(request, ct);

        if (result.Success)
        {
            await repository.UpdateStepCompletedAsync(stepId, ct);
        }
        else
        {
            await repository.UpdateStepFailedAsync(stepId, result.ErrorMessage ?? "Ansible handoff failed.", ct);
            AutomationTelemetry.RecordStepFailure(AnsibleHandoffWorkflowName, "ansible_handoff");
        }

        await repository.UpdateTotalMachinesAsync(runId.Value, 1, ct);
        await repository.AddMachineResultAsync(
            runId.Value,
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(request.TargetScope) ? "ansible-scope:all" : $"ansible-scope:{request.TargetScope}",
            result.Success,
            result.ErrorMessage,
            JsonSerializer.Serialize(new
            {
                result.Operation,
                result.Playbook,
                result.CommandLine,
                result.ExitCode,
                result.TimedOut,
                result.Cancelled,
                request.DryRun,
                request.ExecutionTimeoutSeconds,
                request.CancelOnTimeout,
                request.ApprovedBy,
                request.ApprovalReason,
                request.ChangeTicket
            }),
            ct);
        AutomationTelemetry.RecordMachineOutcome(AnsibleHandoffWorkflowName, result.Success);

        var outputJson = JsonSerializer.Serialize(new
        {
            workflow = AnsibleHandoffWorkflowName,
            generatedUtc = DateTime.UtcNow,
            request.Operation,
            request.TargetScope,
            request.DryRun,
            request.ExecutionTimeoutSeconds,
            request.CancelOnTimeout,
            request.ApprovedBy,
            request.ApprovalReason,
            request.ChangeTicket,
            execution = result
        });

        var markdownLines = new List<string>
        {
            "# Ansible Handoff Report",
            string.Empty,
            $"- Operation: {request.Operation}",
            $"- TargetScope: {(string.IsNullOrWhiteSpace(request.TargetScope) ? "all" : request.TargetScope)}",
            $"- DryRun: {request.DryRun}",
            $"- ExecutionTimeoutSeconds: {(request.ExecutionTimeoutSeconds.HasValue ? request.ExecutionTimeoutSeconds.Value : 0)}",
            $"- CancelOnTimeout: {request.CancelOnTimeout}",
            $"- ApprovedBy: {(string.IsNullOrWhiteSpace(request.ApprovedBy) ? "n/a" : request.ApprovedBy)}",
            $"- ChangeTicket: {(string.IsNullOrWhiteSpace(request.ChangeTicket) ? "n/a" : request.ChangeTicket)}",
            $"- Success: {result.Success}",
            string.Empty,
            "## Execution",
            $"- Playbook: {(string.IsNullOrWhiteSpace(result.Playbook) ? "n/a" : result.Playbook)}",
            $"- Command: {(string.IsNullOrWhiteSpace(result.CommandLine) ? "n/a" : result.CommandLine)}",
            $"- ExitCode: {(result.ExitCode.HasValue ? result.ExitCode.Value : -1)}",
            $"- TimedOut: {result.TimedOut}",
            $"- Cancelled: {result.Cancelled}"
        };

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            markdownLines.Add($"- Error: {result.ErrorMessage}");
        }

        await repository.UpdateRunCompletedAsync(
            runId.Value,
            AutomationRunStateKind.Completed.ToString(),
            completedMachines: result.Success ? 1 : 0,
            failedMachines: result.Success ? 0 : 1,
            outputJson,
            string.Join(Environment.NewLine, markdownLines),
            ct);

        await RecordAuditEventAsync(
            correlationId,
            AuditAction.JobCompleted,
            result.Success ? AuditOutcome.Success : AuditOutcome.Failure,
            $"Automation run completed for workflow '{AnsibleHandoffWorkflowName}'.",
            runId,
            AnsibleHandoffWorkflowName,
            ct,
            result.Success ? null : result.ErrorMessage);
        AutomationTelemetry.RecordRunCompleted(AnsibleHandoffWorkflowName, success: result.Success, stopwatch.Elapsed.TotalMilliseconds);
    }

    private async Task<string?> GenerateSummaryAsync(
        AutomationRunId runId,
        string outputJson,
        IAutomationRunRepository repository,
        ILLMClient llmClient,
        CancellationToken ct)
    {
        var stepId = Guid.NewGuid();
        await repository.AddStepAsync(runId.Value, stepId, "llm_summarize_health", ct);
        await repository.UpdateStepStateAsync(stepId, AutomationStepState.Running.ToString(), ct);

        Exception? lastError = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var request = new LLMGenerationRequest(
                    Prompt: $"Summarize the fleet health report as concise operational notes. Include key risks and suggested next checks. Report JSON: {outputJson}",
                    SystemPrompt: "You are an SRE assistant. Return concise markdown-safe text only.",
                    MaxTokens: 256,
                    Temperature: 0.2);

                var result = await llmClient.GenerateAsync(request, ct);
                if (result.Success && !string.IsNullOrWhiteSpace(result.Content))
                {
                    await repository.UpdateStepCompletedAsync(stepId, ct);
                    return result.Content.Trim();
                }

                lastError = new InvalidOperationException(result.Error ?? "LLM returned no summary content.");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        _logger.LogWarning(lastError, "LLM summary generation failed for run {RunId}", runId.Value);
        await repository.UpdateStepFailedAsync(stepId, lastError?.Message ?? "Unknown summary failure.", ct);
        AutomationTelemetry.RecordStepFailure(HealthReportWorkflowName, "llm_summarize_health");
        return null;
    }

    private static async Task<List<Machine>> ResolveTargetsAsync(
        AutomationRunId runId,
        HealthReportRunRequest request,
        IAutomationRunRepository repository,
        IInventoryService inventoryService,
        string workflowName,
        CancellationToken ct)
    {
        var stepId = Guid.NewGuid();
        await repository.AddStepAsync(runId.Value, stepId, "resolve_targets", ct);
        await repository.UpdateStepStateAsync(stepId, AutomationStepState.Running.ToString(), ct);

        try
        {
            var maxTargets = Math.Clamp(request.MaxTargets, 1, 500);
            var query = new MachineQuery(Page: 1, PageSize: maxTargets);
            var page = await inventoryService.QueryAsync(query, ct);

            IEnumerable<Machine> targets = page.Items;
            if (request.TargetMachineIds is { Count: > 0 })
            {
                var allowed = request.TargetMachineIds.ToHashSet();
                targets = targets.Where(m => allowed.Contains(m.Id));
            }

            if (!string.IsNullOrWhiteSpace(request.Tag))
            {
                targets = targets.Where(machine => MatchesTag(machine, request.Tag));
            }

            var resolved = targets.ToList();
            await repository.UpdateStepCompletedAsync(stepId, ct);
            return resolved;
        }
        catch (Exception ex)
        {
            await repository.UpdateStepFailedAsync(stepId, ex.Message, ct);
            AutomationTelemetry.RecordStepFailure(workflowName, "resolve_targets");
            throw;
        }
    }

    private async Task<Dictionary<Guid, Machine>> GatherMetricsAsync(
        AutomationRunId runId,
        List<Machine> targets,
        IAutomationRunRepository repository,
        IInventoryService inventoryService,
        CancellationToken ct)
    {
        var stepId = Guid.NewGuid();
        await repository.AddStepAsync(runId.Value, stepId, "gather_basic_metrics", ct);
        await repository.UpdateStepStateAsync(stepId, AutomationStepState.Running.ToString(), ct);

        var results = new Dictionary<Guid, Machine>(targets.Count);
        foreach (var machine in targets)
        {
            try
            {
                var refreshed = await inventoryService.RefreshMetadataAsync(machine.Id, ct);
                results[machine.Id] = refreshed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh metadata for machine {MachineId}", machine.Id);
            }
        }

        await repository.UpdateStepCompletedAsync(stepId, ct);
        return results;
    }

    private async Task<Dictionary<Guid, int>> GatherServiceStatusAsync(
        AutomationRunId runId,
        List<Machine> targets,
        IAutomationRunRepository repository,
        IServiceController serviceController,
        CancellationToken ct)
    {
        var stepId = Guid.NewGuid();
        await repository.AddStepAsync(runId.Value, stepId, "gather_service_status", ct);
        await repository.UpdateStepStateAsync(stepId, AutomationStepState.Running.ToString(), ct);

        var results = new Dictionary<Guid, int>(targets.Count);
        foreach (var machine in targets)
        {
            try
            {
                var target = ToMachineTarget(machine);
                var services = await serviceController.ListServicesAsync(target, filter: null, ct);
                results[machine.Id] = services.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to gather services for machine {MachineId}", machine.Id);
                results[machine.Id] = 0;
            }
        }

        await repository.UpdateStepCompletedAsync(stepId, ct);
        return results;
    }

    private static bool MatchesTag(Machine machine, string tag)
    {
        return machine.Tags.Any(kvp =>
            kvp.Key.Contains(tag, StringComparison.OrdinalIgnoreCase)
            || kvp.Value.Contains(tag, StringComparison.OrdinalIgnoreCase));
    }

    private static MachineTarget ToMachineTarget(Machine machine)
    {
        return new MachineTarget(
            machine.Id,
            machine.Hostname,
            machine.OsType,
            machine.ConnectionMode,
            machine.Protocol,
            machine.Port,
            machine.CredentialId);
    }

    private async Task RecordAuditEventAsync(
        string correlationId,
        AuditAction action,
        AuditOutcome outcome,
        string detail,
        AutomationRunId runId,
        string workflowName,
        CancellationToken ct,
        string? errorMessage = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var auditLogger = scope.ServiceProvider.GetService<IAuditLogger>();
        if (auditLogger is null)
        {
            return;
        }

        var properties = new Dictionary<string, string>
        {
            ["workflow"] = workflowName,
            ["automationRunId"] = runId.Value.ToString("D")
        };

        var auditEvent = new AuditEvent(
            EventId: Guid.NewGuid(),
            TimestampUtc: DateTime.UtcNow,
            CorrelationId: correlationId,
            Action: action,
            ActorIdentity: "automation.engine",
            TargetMachineId: null,
            TargetMachineName: null,
            Detail: detail,
            Properties: properties,
            Outcome: outcome,
            ErrorMessage: errorMessage);

        await auditLogger.RecordAsync(auditEvent, ct);
    }

    // ── Phase 3 — Safe NL Planning ───────────────────────────────────────────

    public async Task<WorkflowPlan> CreatePlanAsync(CreatePlanRequest request, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
            throw new InvalidOperationException("Automation is currently disabled.");

        if (string.IsNullOrWhiteSpace(request.Objective))
            throw new ArgumentException("Objective must not be empty.", nameof(request));

        var correlationId = Guid.NewGuid().ToString("N");

        WorkflowPlan plan;
        PolicyEvaluation policy;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var planner = scope.ServiceProvider.GetRequiredService<IWorkflowPlanner>();
            plan = await planner.CreatePlanAsync(request, ct);
        }
        catch (PlanSchemaValidationException ex)
        {
            throw new InvalidOperationException(
                $"Planner schema validation failed: {ex.Message}", ex);
        }

        policy = PlanPolicyEngine.Evaluate(plan.Steps);

        // Override the risk level and status based on policy evaluation.
        var finalStatus = policy.Allowed ? PlanStatus.PendingApproval : PlanStatus.Rejected;
        var rejectionReason = policy.Allowed ? null : string.Join("; ", policy.Violations);

        plan = plan with
        {
            RiskLevel = policy.RiskLevel,
            Status = finalStatus,
            RejectionReason = rejectionReason,
        };

        using (var scope = _serviceProvider.CreateScope())
        {
            var planRepository = scope.ServiceProvider.GetRequiredService<IPlanRepository>();
            var stepsJson = System.Text.Json.JsonSerializer.Serialize(
                plan.Steps.Select(s => new
                {
                    name = s.Name,
                    kind = s.Kind.ToString(),
                    description = s.Description,
                    parameters = s.Parameters
                }));

            await planRepository.CreatePlanAsync(
                plan.Id.Value,
                plan.Objective,
                stepsJson,
                plan.RiskLevel.ToString(),
                plan.PlanHash,
                plan.Status.ToString(),
                correlationId,
                ct);
        }

        _logger.LogInformation(
            "Plan {PlanId} created with status {Status} and risk {Risk} for objective: {Objective}",
            plan.Id.Value, plan.Status, plan.RiskLevel, plan.Objective);

        return plan;
    }

    public async Task<WorkflowPlan> ApprovePlanAsync(WorkflowPlanId planId, ApprovePlanRequest request, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(ct))
            throw new InvalidOperationException("Automation is currently disabled.");

        using var scope = _serviceProvider.CreateScope();
        var planRepository = scope.ServiceProvider.GetRequiredService<IPlanRepository>();

        var plan = await planRepository.GetPlanAsync(planId.Value, ct)
            ?? throw new InvalidOperationException($"Plan {planId.Value} not found.");

        if (plan.Status != PlanStatus.PendingApproval)
        {
            throw new InvalidOperationException(
                $"Plan {planId.Value} cannot be approved from status '{plan.Status}'. " +
                "Only PendingApproval plans may be approved.");
        }

        if (!string.Equals(plan.PlanHash, request.ExpectedHash, StringComparison.OrdinalIgnoreCase))
        {
            await planRepository.UpdatePlanStatusAsync(
                planId.Value,
                PlanStatus.Rejected.ToString(),
                approvedUtc: null,
                rejectionReason: "Plan hash mismatch - plan may have been tampered with.",
                ct);

            throw new InvalidOperationException(
                $"Plan {planId.Value} approval rejected: hash mismatch. " +
                $"Expected '{request.ExpectedHash}', stored '{plan.PlanHash}'.");
        }

        var approvedUtc = DateTime.UtcNow;
        await planRepository.UpdatePlanStatusAsync(
            planId.Value,
            PlanStatus.Approved.ToString(),
            approvedUtc,
            rejectionReason: null,
            ct);

        _logger.LogInformation("Plan {PlanId} approved at {ApprovedUtc}", planId.Value, approvedUtc);

        _ = Task.Run(() => ExecuteApprovedPlanAsync(planId), CancellationToken.None);

        return plan with { Status = PlanStatus.Approved, ApprovedUtc = approvedUtc };
    }

    public async Task<WorkflowPlan?> GetPlanAsync(WorkflowPlanId planId, CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var planRepository = scope.ServiceProvider.GetRequiredService<IPlanRepository>();
        return await planRepository.GetPlanAsync(planId.Value, ct);
    }

    private async Task ExecuteApprovedPlanAsync(WorkflowPlanId planId)
    {
        using var scope = _serviceProvider.CreateScope();
        var planRepository = scope.ServiceProvider.GetRequiredService<IPlanRepository>();

        try
        {
            var dbPlan = await planRepository.GetPlanAsync(planId.Value, CancellationToken.None);
            if (dbPlan is null)
            {
                _logger.LogWarning("Approved plan {PlanId} was not found at dispatch time.", planId.Value);
                return;
            }

            await planRepository.UpdatePlanStatusAsync(
                planId.Value,
                PlanStatus.Executing.ToString(),
                approvedUtc: dbPlan.ApprovedUtc,
                rejectionReason: null,
                CancellationToken.None);

            var plan = dbPlan with { Status = PlanStatus.Executing };

            var runId = await DispatchPlanToRunAsync(plan, CancellationToken.None);
            var run = await WaitForRunTerminalAsync(runId, TimeSpan.FromMinutes(3), CancellationToken.None);

            var terminalStatus = run.State == AutomationRunStateKind.Completed
                ? PlanStatus.Completed
                : PlanStatus.Failed;

            await planRepository.UpdatePlanStatusAsync(
                planId.Value,
                terminalStatus.ToString(),
                approvedUtc: dbPlan.ApprovedUtc,
                rejectionReason: run.ErrorMessage,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Approved plan {PlanId} dispatch failed.", planId.Value);
            await planRepository.UpdatePlanStatusAsync(
                planId.Value,
                PlanStatus.Failed.ToString(),
                approvedUtc: null,
                rejectionReason: ex.Message,
                CancellationToken.None);
        }
    }

    private async Task<AutomationRunId> DispatchPlanToRunAsync(WorkflowPlan plan, CancellationToken ct)
    {
        // Phase 4 execution bridge:
        // map approved plan shapes to supported workflow entry points.
        var tag = plan.Steps
            .SelectMany(s => s.Parameters)
            .FirstOrDefault(kv => string.Equals(kv.Key, "tag", StringComparison.OrdinalIgnoreCase))
            .Value;

        var maxTargetsRaw = plan.Steps
            .SelectMany(s => s.Parameters)
            .FirstOrDefault(kv => string.Equals(kv.Key, "maxTargets", StringComparison.OrdinalIgnoreCase))
            .Value;

        var maxTargets = 100;
        if (!string.IsNullOrWhiteSpace(maxTargetsRaw) && int.TryParse(maxTargetsRaw, out var parsedMaxTargets))
        {
            maxTargets = Math.Clamp(parsedMaxTargets, 1, 500);
        }

        var restartStep = plan.Steps.FirstOrDefault(s => s.Kind == PlanStepKind.RestartService);
        if (restartStep is not null)
        {
            if (!restartStep.Parameters.TryGetValue("serviceName", out var plannedServiceName)
                || string.IsNullOrWhiteSpace(plannedServiceName))
            {
                throw new InvalidOperationException("RestartService plan step is missing required parameter 'serviceName'.");
            }

            var serviceRequest = new EnsureServiceRunningRunRequest(
                ServiceName: plannedServiceName,
                Tag: tag,
                MaxTargets: maxTargets,
                AttemptRestart: true);

            return await StartEnsureServiceRunningAsync(serviceRequest, ct);
        }

        var patchStep = plan.Steps.FirstOrDefault(s => s.Kind == PlanStepKind.ApplyPatch);
        if (patchStep is not null)
        {
            var patchTag = patchStep.Parameters.TryGetValue("tag", out var patchTagRaw)
                && !string.IsNullOrWhiteSpace(patchTagRaw)
                ? patchTagRaw
                : tag;

            var patchMaxTargets = maxTargets;
            if (patchStep.Parameters.TryGetValue("maxTargets", out var patchMaxTargetsRaw)
                && int.TryParse(patchMaxTargetsRaw, out var parsedPatchMaxTargets))
            {
                patchMaxTargets = Math.Clamp(parsedPatchMaxTargets, 1, 500);
            }

            var dryRun = patchStep.Parameters.TryGetValue("dryRun", out var dryRunRaw)
                && bool.TryParse(dryRunRaw, out var parsedDryRun)
                && parsedDryRun;

            var allowReboot = patchStep.Parameters.TryGetValue("allowReboot", out var allowRebootRaw)
                && bool.TryParse(allowRebootRaw, out var parsedAllowReboot)
                && parsedAllowReboot;

            var targetMachineIds = patchStep.Parameters.TryGetValue("targetMachineIds", out var targetMachineIdsRaw)
                ? ParseTargetMachineIds(targetMachineIdsRaw)
                : null;

            var patchRequest = new PatchAllRunRequest(
                TargetMachineIds: targetMachineIds,
                Tag: patchTag,
                MaxTargets: patchMaxTargets,
                DryRun: dryRun,
                AllowReboot: allowReboot);

            return await StartPatchAllAsync(patchRequest, ct);
        }

        var ansibleStep = plan.Steps.FirstOrDefault(s =>
            s.Kind == PlanStepKind.RunScript
            && s.Parameters.ContainsKey("operation"));
        if (ansibleStep is not null)
        {
            if (!ansibleStep.Parameters.TryGetValue("operation", out var operation)
                || string.IsNullOrWhiteSpace(operation))
            {
                throw new InvalidOperationException("RunScript plan step is missing required parameter 'operation'.");
            }

            ansibleStep.Parameters.TryGetValue("targetScope", out var targetScope);
            ansibleStep.Parameters.TryGetValue("extraVarsJson", out var extraVarsJson);
            ansibleStep.Parameters.TryGetValue("approvedBy", out var approvedBy);
            ansibleStep.Parameters.TryGetValue("approvalReason", out var approvalReason);
            ansibleStep.Parameters.TryGetValue("changeTicket", out var changeTicket);
            ansibleStep.Parameters.TryGetValue("executionTimeoutSeconds", out var executionTimeoutSecondsRaw);
            ansibleStep.Parameters.TryGetValue("cancelOnTimeout", out var cancelOnTimeoutRaw);

            var dryRun = !(ansibleStep.Parameters.TryGetValue("dryRun", out var dryRunRaw)
                && bool.TryParse(dryRunRaw, out var parsedDryRun)
                && !parsedDryRun);

            int? executionTimeoutSeconds = null;
            if (int.TryParse(executionTimeoutSecondsRaw, out var parsedExecutionTimeoutSeconds))
            {
                executionTimeoutSeconds = Math.Clamp(parsedExecutionTimeoutSeconds, 5, 3600);
            }

            var cancelOnTimeout = !(bool.TryParse(cancelOnTimeoutRaw, out var parsedCancelOnTimeout)
                && !parsedCancelOnTimeout);

            var handoffRequest = new AnsibleHandoffRunRequest(
                Operation: operation,
                TargetScope: targetScope,
                ExtraVarsJson: extraVarsJson,
                DryRun: dryRun,
                ExecutionTimeoutSeconds: executionTimeoutSeconds,
                CancelOnTimeout: cancelOnTimeout,
                ApproveAndRun: true,
                ApprovedBy: string.IsNullOrWhiteSpace(approvedBy) ? "plan-approval" : approvedBy,
                ApprovalReason: string.IsNullOrWhiteSpace(approvalReason) ? "Approved plan dispatch" : approvalReason,
                ChangeTicket: changeTicket);

            return await StartAnsibleHandoffAsync(handoffRequest, ct);
        }

        var request = new HealthReportRunRequest(Tag: tag, MaxTargets: maxTargets);
        return await StartHealthReportAsync(request, ct);
    }

    private static List<Guid>? ParseTargetMachineIds(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var ids = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty)
            .Where(guid => guid != Guid.Empty)
            .Distinct()
            .ToList();

        return ids.Count == 0 ? null : ids;
    }

    private async Task<AutomationRun> WaitForRunTerminalAsync(
        AutomationRunId runId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var stopAt = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < stopAt)
        {
            var run = await GetRunAsync(runId, ct);
            if (run is not null && run.State is AutomationRunStateKind.Completed or AutomationRunStateKind.Failed)
            {
                return run;
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException($"Dispatched run {runId.Value} did not reach terminal state within {timeout}.");
    }
}

