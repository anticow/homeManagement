using HomeManagement.Automation;
using HomeManagement.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Broker.Host.Endpoints;

public static class AutomationEndpoints
{
    private static readonly HashSet<string> AllowedAnsibleOperations =
    [
        "proxmox.vm.provision",
        "k3s.control-plane.create",
        "k3s.control-plane.resume",
        "k3s.worker.add",
        "infrastructure.remediate"
    ];

    public static void MapAutomationEndpoints(this WebApplication app)
    {
        MapRunEndpoints(app);
        MapPlanEndpoints(app);
        MapDashboardEndpoints(app);
    }

    // ── Run endpoints (Phase 1 / 2) ──────────────────────────────────────────

    private static void MapRunEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/automation/runs")
            .WithTags("Automation")
            .RequireAuthorization();

        group.MapGet("/", async (int page, int pageSize, IAutomationEngine engine, CancellationToken ct) =>
        {
            var results = await engine.ListRunsAsync(page <= 0 ? 1 : page, pageSize <= 0 ? 25 : pageSize, ct);
            return Results.Ok(results);
        });

        group.MapGet("/{id:guid}", async (Guid id, IAutomationEngine engine, CancellationToken ct) =>
        {
            var run = await engine.GetRunAsync(new AutomationRunId(id), ct);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        group.MapGet("/{id:guid}/output", async (Guid id, IAutomationEngine engine, CancellationToken ct) =>
        {
            var run = await engine.GetRunAsync(new AutomationRunId(id), ct);
            if (run is null)
            {
                return Results.NotFound();
            }

            return string.IsNullOrWhiteSpace(run.OutputJson)
                ? Results.NotFound(new { error = "Run output is not available yet." })
                : Results.Text(run.OutputJson, "application/json");
        });

        group.MapGet("/{id:guid}/summary", async (Guid id, IAutomationEngine engine, CancellationToken ct) =>
        {
            var run = await engine.GetRunAsync(new AutomationRunId(id), ct);
            if (run is null)
            {
                return Results.NotFound();
            }

            return string.IsNullOrWhiteSpace(run.OutputMarkdown)
                ? Results.NotFound(new { error = "Run summary is not available yet." })
                : Results.Text(run.OutputMarkdown, "text/markdown");
        });

        group.MapPost("/health-report", async (HealthReportRunRequest request, IAutomationEngine engine, CancellationToken ct) =>
        {
            if (!await engine.IsEnabledAsync(ct))
            {
                return Results.BadRequest(new { error = "Automation is disabled. Set Automation:Enabled=true and AI:Enabled=true." });
            }

            var runId = await engine.StartHealthReportAsync(request, ct);
            return Results.Accepted($"/api/automation/runs/{runId.Value}", new { runId = runId.Value });
        });

        group.MapPost("/service-ensure-running", async (EnsureServiceRunningRunRequest request, IAutomationEngine engine, CancellationToken ct) =>
        {
            if (!await engine.IsEnabledAsync(ct))
            {
                return Results.BadRequest(new { error = "Automation is disabled. Set Automation:Enabled=true and AI:Enabled=true." });
            }

            if (string.IsNullOrWhiteSpace(request.ServiceName))
            {
                return Results.BadRequest(new { error = "ServiceName must not be empty." });
            }

            var runId = await engine.StartEnsureServiceRunningAsync(request, ct);
            return Results.Accepted($"/api/automation/runs/{runId.Value}", new { runId = runId.Value });
        });

        group.MapPost("/patch-all", async (PatchAllRunRequest request, IAutomationEngine engine, CancellationToken ct) =>
        {
            if (!await engine.IsEnabledAsync(ct))
            {
                return Results.BadRequest(new { error = "Automation is disabled. Set Automation:Enabled=true and AI:Enabled=true." });
            }

            var hasTargetIds = request.TargetMachineIds is { Count: > 0 };
            var hasTag = !string.IsNullOrWhiteSpace(request.Tag);
            if (!hasTargetIds && !hasTag)
            {
                return Results.BadRequest(new { error = "Provide at least one target machine id or a non-empty tag." });
            }

            var runId = await engine.StartPatchAllAsync(request, ct);
            return Results.Accepted($"/api/automation/runs/{runId.Value}", new { runId = runId.Value });
        });

        group.MapPost("/haos-health-status", async (HaosHealthStatusRunRequest request, IAutomationEngine engine, CancellationToken ct) =>
        {
            if (!await engine.IsEnabledAsync(ct))
            {
                return Results.BadRequest(new { error = "Automation is disabled. Set Automation:Enabled=true and AI:Enabled=true." });
            }

            var runId = await engine.StartHaosHealthStatusAsync(request, ct);
            return Results.Accepted($"/api/automation/runs/{runId.Value}", new { runId = runId.Value });
        });

        group.MapPost("/haos-entity-snapshot", async (HaosEntitySnapshotRunRequest request, IAutomationEngine engine, CancellationToken ct) =>
        {
            if (!await engine.IsEnabledAsync(ct))
            {
                return Results.BadRequest(new { error = "Automation is disabled. Set Automation:Enabled=true and AI:Enabled=true." });
            }

            var maxEntities = Math.Clamp(request.MaxEntities, 1, 2000);
            var runId = await engine.StartHaosEntitySnapshotAsync(request with { MaxEntities = maxEntities }, ct);
            return Results.Accepted($"/api/automation/runs/{runId.Value}", new { runId = runId.Value });
        });

        group.MapPost("/ansible-handoff", async (AnsibleHandoffRunRequest request, IAutomationEngine engine, CancellationToken ct) =>
        {
            if (!await engine.IsEnabledAsync(ct))
            {
                return Results.BadRequest(new { error = "Automation is disabled. Set Automation:Enabled=true and AI:Enabled=true." });
            }

            if (string.IsNullOrWhiteSpace(request.Operation))
            {
                return Results.BadRequest(new { error = "Operation must not be empty." });
            }

            if (!AllowedAnsibleOperations.Contains(request.Operation))
            {
                return Results.BadRequest(new { error = "Operation is not allowlisted for Ansible handoff." });
            }

            if (request.ExecutionTimeoutSeconds.HasValue)
            {
                if (request.ExecutionTimeoutSeconds.Value is < 5 or > 3600)
                {
                    return Results.BadRequest(new { error = "ExecutionTimeoutSeconds must be between 5 and 3600 when provided." });
                }

                if (!request.CancelOnTimeout)
                {
                    return Results.BadRequest(new { error = "CancelOnTimeout must be true when ExecutionTimeoutSeconds is provided." });
                }
            }

            if (!request.ApproveAndRun)
            {
                return Results.BadRequest(new { error = "ApproveAndRun must be true for ansible handoff execution." });
            }

            if (string.IsNullOrWhiteSpace(request.ApprovedBy) || string.IsNullOrWhiteSpace(request.ApprovalReason))
            {
                return Results.BadRequest(new { error = "ApprovedBy and ApprovalReason are required." });
            }

            var runId = await engine.StartAnsibleHandoffAsync(request, ct);
            return Results.Accepted($"/api/automation/runs/{runId.Value}", new { runId = runId.Value });
        });
    }

    // ── Plan endpoints (Phase 3) ─────────────────────────────────────────────

    private static void MapPlanEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/automation/plans")
            .WithTags("Automation")
            .RequireAuthorization();

        /// POST /api/automation/plans
        /// Generate a structured workflow plan from a natural-language objective.
        /// Returns the plan in PendingApproval (or Rejected) state — never executes.
        group.MapPost("/", async (CreatePlanRequest request, IAutomationEngine engine, CancellationToken ct) =>
        {
            if (!await engine.IsEnabledAsync(ct))
                return Results.BadRequest(new { error = "Automation is disabled." });

            if (string.IsNullOrWhiteSpace(request.Objective))
                return Results.BadRequest(new { error = "Objective must not be empty." });

            WorkflowPlan plan;
            try
            {
                plan = await engine.CreatePlanAsync(request, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }

            if (plan.Status == PlanStatus.Rejected)
            {
                return Results.UnprocessableEntity(new
                {
                    planId = plan.Id.Value,
                    status = plan.Status.ToString(),
                    riskLevel = plan.RiskLevel.ToString(),
                    rejectionReason = plan.RejectionReason,
                    planHash = plan.PlanHash,
                    steps = plan.Steps,
                });
            }

            return Results.Created(
                $"/api/automation/plans/{plan.Id.Value}",
                new
                {
                    planId = plan.Id.Value,
                    status = plan.Status.ToString(),
                    riskLevel = plan.RiskLevel.ToString(),
                    planHash = plan.PlanHash,
                    steps = plan.Steps,
                });
        });

        /// GET /api/automation/plans/{id}
        group.MapGet("/{id:guid}", async (Guid id, IAutomationEngine engine, CancellationToken ct) =>
        {
            var plan = await engine.GetPlanAsync(new WorkflowPlanId(id), ct);
            return plan is null ? Results.NotFound() : Results.Ok(plan);
        });

        /// POST /api/automation/plans/{id}/approve
        /// Verify the plan hash and policy, approve, and enqueue execution.
        /// The request body must contain the SHA-256 hash the client received at plan creation time.
        group.MapPost("/{id:guid}/approve", async (Guid id, ApprovePlanRequest request, IAutomationEngine engine, CancellationToken ct) =>
        {
            if (!await engine.IsEnabledAsync(ct))
                return Results.BadRequest(new { error = "Automation is disabled." });

            if (string.IsNullOrWhiteSpace(request.ExpectedHash))
                return Results.BadRequest(new { error = "ExpectedHash must not be empty." });

            try
            {
                var plan = await engine.ApprovePlanAsync(new WorkflowPlanId(id), request, ct);
                return Results.Ok(new
                {
                    planId = plan.Id.Value,
                    status = plan.Status.ToString(),
                    approvedUtc = plan.ApprovedUtc,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static void MapDashboardEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/automation/dashboard")
            .WithTags("Automation")
            .RequireAuthorization();

        group.MapGet("/summary", async (int hours, HomeManagementDbContext db, CancellationToken ct) =>
        {
            var lookbackHours = Math.Clamp(hours <= 0 ? 24 : hours, 1, 24 * 30);
            var sinceUtc = DateTime.UtcNow.AddHours(-lookbackHours);

            var runs = await db.AutomationRuns
                .AsNoTracking()
                .Where(r => r.StartedUtc >= sinceUtc)
                .Select(r => new
                {
                    r.WorkflowType,
                    r.State,
                    r.FailedMachines,
                    r.StartedUtc,
                    r.CompletedUtc
                })
                .ToListAsync(ct);

            var summary = runs
                .GroupBy(r => r.WorkflowType)
                .Select(g =>
                {
                    var completed = g.Where(r => string.Equals(r.State, "Completed", StringComparison.OrdinalIgnoreCase)).ToList();
                    var successCount = completed.Count(r => r.FailedMachines == 0);
                    var failureCount = g.Count() - successCount;
                    var durations = completed
                        .Where(r => r.CompletedUtc.HasValue)
                        .Select(r => (r.CompletedUtc!.Value - r.StartedUtc).TotalMilliseconds)
                        .ToList();

                    return new
                    {
                        workflow = g.Key,
                        runVolume = g.Count(),
                        successCount,
                        failureCount,
                        avgDurationMs = durations.Count == 0 ? 0 : durations.Average(),
                        p95DurationMs = Percentile(durations, 95)
                    };
                })
                .OrderBy(s => s.workflow)
                .ToList();

            return Results.Ok(new
            {
                sinceUtc,
                lookbackHours,
                workflows = summary
            });
        });

        group.MapGet("/step-failures", async (int hours, HomeManagementDbContext db, CancellationToken ct) =>
        {
            var lookbackHours = Math.Clamp(hours <= 0 ? 24 : hours, 1, 24 * 30);
            var sinceUtc = DateTime.UtcNow.AddHours(-lookbackHours);

            var failures = await (
                from step in db.AutomationRunSteps.AsNoTracking()
                join run in db.AutomationRuns.AsNoTracking() on step.RunId equals run.Id
                where step.StartedUtc >= sinceUtc
                      && step.State == "Failed"
                group step by new { run.WorkflowType, step.StepName }
                into grouped
                orderby grouped.Count() descending
                select new
                {
                    workflow = grouped.Key.WorkflowType,
                    step = grouped.Key.StepName,
                    failures = grouped.Count()
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                sinceUtc,
                lookbackHours,
                stepFailures = failures
            });
        });

        group.MapGet("/machine-outcomes/{workflowName}", async (
            string workflowName,
            int hours,
            int page,
            int pageSize,
            HomeManagementDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(workflowName))
            {
                return Results.BadRequest(new { error = "workflowName is required." });
            }

            var normalizedPage = Math.Max(1, page);
            var normalizedPageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 200);
            var lookbackHours = Math.Clamp(hours <= 0 ? 24 : hours, 1, 24 * 30);
            var sinceUtc = DateTime.UtcNow.AddHours(-lookbackHours);

            var query =
                from result in db.AutomationMachineResults.AsNoTracking()
                join run in db.AutomationRuns.AsNoTracking() on result.RunId equals run.Id
                where run.StartedUtc >= sinceUtc
                      && run.WorkflowType == workflowName
                select new
                {
                    runId = run.Id,
                    runStartedUtc = run.StartedUtc,
                    machineId = result.MachineId,
                    machineName = result.MachineName,
                    success = result.Success,
                    errorMessage = result.ErrorMessage,
                    resultDataJson = result.ResultDataJson
                };

            var total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(x => x.runStartedUtc)
                .ThenBy(x => x.machineName)
                .Skip((normalizedPage - 1) * normalizedPageSize)
                .Take(normalizedPageSize)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                workflow = workflowName,
                sinceUtc,
                lookbackHours,
                page = normalizedPage,
                pageSize = normalizedPageSize,
                total,
                items
            });
        });
    }

    private static double Percentile(List<double> values, int percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.OrderBy(v => v).ToArray();
        var index = (int)Math.Ceiling(percentile / 100.0 * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }
}
