using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace HomeManagement.Orchestration;

/// <summary>
/// Coordinates multi-step operations across machines using Quartz.NET for scheduling.
/// Provides ad-hoc job submission and cron-based recurring schedules.
/// </summary>
internal sealed class JobSchedulerService : IJobScheduler, IDisposable
{
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<JobSchedulerService> _logger;
    private readonly Subject<JobProgressEvent> _progressSubject = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobs = new();

    public JobSchedulerService(
        ISchedulerFactory schedulerFactory,
        IServiceScopeFactory scopeFactory,
        ICorrelationContext correlation,
        ILogger<JobSchedulerService> logger)
    {
        _schedulerFactory = schedulerFactory;
        _scopeFactory = scopeFactory;
        _correlation = correlation;
        _logger = logger;
    }

    public IObservable<JobProgressEvent> ProgressStream => _progressSubject.AsObservable();

    public IObservable<JobProgressEvent> GetJobProgressStream(JobId jobId) =>
        _progressSubject.Where(e => e.JobId == jobId).AsObservable();

    public async Task<JobId> SubmitAsync(JobDefinition job, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        // ── Idempotency guard ──
        if (job.IdempotencyKey.HasValue)
        {
            var existing = await jobRepo.GetByIdempotencyKeyAsync(job.IdempotencyKey.Value, ct);
            if (existing is not null)
            {
                _logger.LogInformation("[{CorrelationId}] Duplicate submission detected (idempotency key {Key}), returning existing job {JobId}",
                    _correlation.CorrelationId, job.IdempotencyKey.Value, existing.Id);
                return existing.Id;
            }
        }

        var jobId = JobId.New();
        var now = DateTime.UtcNow;

        var definitionJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            job.TargetMachineIds,
            job.Parameters,
            job.MaxParallelism,
            job.IdempotencyKey
        });

        var status = new JobStatus(
            Id: jobId,
            Name: job.Name,
            Type: job.Type,
            State: JobState.Queued,
            SubmittedUtc: now,
            StartedUtc: null,
            CompletedUtc: null,
            TotalTargets: job.TargetMachineIds.Count,
            CompletedTargets: 0,
            FailedTargets: 0,
            MachineResults: [],
            DefinitionJson: definitionJson);

        await jobRepo.AddAsync(status, ct);
        await jobRepo.SaveChangesAsync(ct);

        _logger.LogInformation("[{CorrelationId}] Job submitted: {JobId} ({Name}, {Type}, {Targets} targets)",
            _correlation.CorrelationId, jobId, job.Name, job.Type, job.TargetMachineIds.Count);

        // Schedule immediate execution via Quartz
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var quartzJob = JobBuilder.Create<JobExecutionQuartzJob>()
            .WithIdentity(jobId.Value.ToString(), "adhoc")
            .UsingJobData("JobId", jobId.Value.ToString())
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"trigger-{jobId.Value}", "adhoc")
            .StartNow()
            .Build();

        await scheduler.ScheduleJob(quartzJob, trigger, ct);
        return jobId;
    }

    public async Task<ScheduleId> ScheduleAsync(JobDefinition job, string cronExpression, CancellationToken ct = default)
    {
        var scheduleId = ScheduleId.New();

        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var quartzJob = JobBuilder.Create<JobExecutionQuartzJob>()
            .WithIdentity(scheduleId.Value.ToString(), "scheduled")
            .UsingJobData("ScheduleId", scheduleId.Value.ToString())
            .UsingJobData("JobName", job.Name)
            .UsingJobData("JobType", job.Type.ToString())
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"cron-{scheduleId.Value}", "scheduled")
            .WithCronSchedule(cronExpression)
            .Build();

        await scheduler.ScheduleJob(quartzJob, trigger, ct);

        _logger.LogInformation("[{CorrelationId}] Schedule created: {ScheduleId} ({Name}, cron={Cron})",
            _correlation.CorrelationId, scheduleId, job.Name, cronExpression);

        return scheduleId;
    }

    public async Task CancelAsync(JobId jobId, CancellationToken ct = default)
    {
        if (_activeJobs.TryRemove(jobId.Value, out var cts))
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        var scheduler = await _schedulerFactory.GetScheduler(ct);
        await scheduler.DeleteJob(new JobKey(jobId.Value.ToString(), "adhoc"), ct);

        using var scope = _scopeFactory.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var status = await jobRepo.GetByIdAsync(jobId.Value, ct);
        if (status is not null && status.State is JobState.Queued or JobState.Running)
        {
            var updated = status with { State = JobState.Cancelled, CompletedUtc = DateTime.UtcNow };
            await jobRepo.UpdateAsync(updated, ct);
            await jobRepo.SaveChangesAsync(ct);
        }

        _logger.LogInformation("[{CorrelationId}] Job cancelled: {JobId}", _correlation.CorrelationId, jobId);
    }

    public async Task UnscheduleAsync(ScheduleId scheduleId, CancellationToken ct = default)
    {
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        await scheduler.DeleteJob(new JobKey(scheduleId.Value.ToString(), "scheduled"), ct);
        _logger.LogInformation("[{CorrelationId}] Schedule removed: {ScheduleId}", _correlation.CorrelationId, scheduleId);
    }

    public async Task<JobStatus> GetStatusAsync(JobId jobId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        return await jobRepo.GetByIdAsync(jobId.Value, ct)
            ?? throw new KeyNotFoundException($"Job {jobId} not found.");
    }

    public async Task<PagedResult<JobSummary>> ListJobsAsync(JobQuery query, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        return await jobRepo.QueryAsync(query, ct);
    }

    public async Task<IReadOnlyList<ScheduledJobSummary>> ListSchedulesAsync(CancellationToken ct = default)
    {
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var jobKeys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.GroupEquals("scheduled"), ct);

        var summaries = new List<ScheduledJobSummary>();
        foreach (var key in jobKeys)
        {
            var triggers = await scheduler.GetTriggersOfJob(key, ct);
            var trigger = triggers.FirstOrDefault();

            summaries.Add(new ScheduledJobSummary(
                Id: new ScheduleId(Guid.Parse(key.Name)),
                Name: key.Name,
                Type: JobType.Custom,
                CronExpression: (trigger as ICronTrigger)?.CronExpressionString ?? "",
                NextFireUtc: trigger?.GetNextFireTimeUtc()?.UtcDateTime,
                LastFireUtc: trigger?.GetPreviousFireTimeUtc()?.UtcDateTime));
        }

        return summaries;
    }

    public void Dispose()
    {
        _progressSubject.Dispose();
        foreach (var cts in _activeJobs.Values)
            cts.Dispose();
        _activeJobs.Clear();
    }
}

/// <summary>
/// Quartz.NET job wrapper that resolves machines and dispatches commands via the
/// <see cref="ICommandBroker"/> for fully async, fire-and-forget execution.
/// Results are persisted to the DB by the broker regardless of UI state.
/// </summary>
internal sealed class JobExecutionQuartzJob : IJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICommandBroker _broker;
    private readonly ILogger<JobExecutionQuartzJob> _logger;

    public JobExecutionQuartzJob(
        IServiceScopeFactory scopeFactory,
        ICommandBroker broker,
        ILogger<JobExecutionQuartzJob> logger)
    {
        _scopeFactory = scopeFactory;
        _broker = broker;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var jobIdString = context.MergedJobDataMap.GetString("JobId");
        if (string.IsNullOrEmpty(jobIdString) || !Guid.TryParse(jobIdString, out var jobGuid))
        {
            _logger.LogWarning("Quartz job executed without valid JobId");
            return;
        }

        var jobId = new JobId(jobGuid);
        _logger.LogInformation("Executing job {JobId}", jobId);

        using var scope = _scopeFactory.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();

        var status = await jobRepo.GetByIdAsync(jobGuid, context.CancellationToken);
        if (status is null)
        {
            _logger.LogWarning("Job {JobId} not found in repository", jobId);
            return;
        }

        // Mark as running
        var running = status with { State = JobState.Running, StartedUtc = DateTime.UtcNow };
        await jobRepo.UpdateAsync(running, context.CancellationToken);
        await jobRepo.SaveChangesAsync(context.CancellationToken);

        // Resolve the definition to get target machine IDs
        var definition = status.DefinitionJson is not null
            ? System.Text.Json.JsonSerializer.Deserialize<JobDefinitionData>(status.DefinitionJson)
            : null;

        if (definition is null || definition.TargetMachineIds.Count == 0)
        {
            _logger.LogWarning("Job {JobId} has no target machines", jobId);
            var empty = running with { State = JobState.Completed, CompletedUtc = DateTime.UtcNow };
            await jobRepo.UpdateAsync(empty, context.CancellationToken);
            await jobRepo.SaveChangesAsync(context.CancellationToken);
            return;
        }

        // Dispatch commands to each target machine via the broker
        foreach (var machineId in definition.TargetMachineIds)
        {
            var machine = await inventory.GetAsync(machineId, context.CancellationToken);
            if (machine is null)
            {
                _logger.LogWarning("Job {JobId}: machine {MachineId} not found, skipping", jobId, machineId);
                continue;
            }

            var target = new MachineTarget(
                machine.Id, machine.Hostname, machine.OsType, machine.ConnectionMode,
                machine.Protocol, machine.Port, machine.CredentialId);

            var command = BuildCommand(status.Type, definition, target);
            if (command is null)
            {
                _logger.LogWarning("Job {JobId}: cannot build command for type {Type}", jobId, status.Type);
                continue;
            }

            var envelope = new CommandEnvelope(
                MachineId: machineId,
                MachineName: machine.Hostname.Value,
                Target: target,
                Command: command,
                JobId: jobGuid,
                Description: $"{status.Type}: {status.Name}");

            await _broker.SubmitAsync(envelope, context.CancellationToken);
        }

        _logger.LogInformation("Job {JobId} dispatched {Count} commands to broker",
            jobId, definition.TargetMachineIds.Count);
    }

    private static RemoteCommand? BuildCommand(
        JobType type, JobDefinitionData definition, MachineTarget target)
    {
        return type switch
        {
            JobType.PatchScan => new RemoteCommand(
                target.OsType == OsType.Windows
                    ? "Get-WindowsUpdate -MicrosoftUpdate | Select-Object KB,Title,Size,MsrcSeverity,IsDownloaded | ConvertTo-Json -Compress"
                    : "apt list --upgradable 2>/dev/null | tail -n +2",
                TimeSpan.FromMinutes(5)),

            JobType.PatchApply => new RemoteCommand(
                target.OsType == OsType.Windows
                    ? "Install-WindowsUpdate -AcceptAll -IgnoreReboot -Confirm:$false | ConvertTo-Json -Compress"
                    : "DEBIAN_FRONTEND=noninteractive apt-get upgrade -y 2>&1",
                TimeSpan.FromMinutes(30),
                ElevationMode.Sudo),

            JobType.ServiceControl => new RemoteCommand(
                target.OsType == OsType.Windows
                    ? "Get-Service | Select-Object Name,DisplayName,Status,StartType | ConvertTo-Json -Compress"
                    : "systemctl list-units --type=service --all --no-pager --no-legend",
                TimeSpan.FromSeconds(60)),

            JobType.MetadataRefresh => new RemoteCommand(
                target.OsType == OsType.Windows
                    ? "Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,OSArchitecture | ConvertTo-Json -Compress"
                    : "uname -a && lscpu && free -b | head -2",
                TimeSpan.FromSeconds(30)),

            _ => null
        };
    }
}

/// <summary>
/// Lightweight deserialization target for the job definition stored in DefinitionJson.
/// </summary>
internal sealed class JobDefinitionData
{
    public List<Guid> TargetMachineIds { get; set; } = [];
    public Dictionary<string, object>? Parameters { get; set; }
    public int MaxParallelism { get; set; } = 5;
}
