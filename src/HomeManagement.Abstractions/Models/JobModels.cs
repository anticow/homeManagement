namespace HomeManagement.Abstractions.Models;

// ── Jobs ──

public record JobId(Guid Value)
{
    public static JobId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public record ScheduleId(Guid Value)
{
    public static ScheduleId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public record JobDefinition(
    string Name,
    JobType Type,
    IReadOnlyList<Guid> TargetMachineIds,
    Dictionary<string, object> Parameters,
    int MaxParallelism = 5,
    RetryPolicy? RetryPolicy = null,
    Guid? IdempotencyKey = null);

public record RetryPolicy(
    int MaxRetries = 3,
    TimeSpan BaseDelay = default,
    TimeSpan MaxDelay = default);

public record JobStatus(
    JobId Id,
    string Name,
    JobType Type,
    JobState State,
    DateTime SubmittedUtc,
    DateTime? StartedUtc,
    DateTime? CompletedUtc,
    int TotalTargets,
    int CompletedTargets,
    int FailedTargets,
    IReadOnlyList<JobMachineResult> MachineResults,
    string? DefinitionJson = null);

public record JobMachineResult(
    Guid MachineId,
    string MachineName,
    bool Success,
    string? ErrorMessage,
    TimeSpan Duration);

public record JobSummary(
    JobId Id,
    string Name,
    JobType Type,
    JobState State,
    DateTime SubmittedUtc,
    DateTime? CompletedUtc,
    int TotalTargets,
    int SuccessCount,
    int FailCount);

public record JobQuery(
    JobType? Type = null,
    JobState? State = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = 50);

public record ScheduledJobSummary(
    ScheduleId Id,
    string Name,
    JobType Type,
    string CronExpression,
    DateTime? NextFireUtc,
    DateTime? LastFireUtc);

public record JobProgressEvent(
    JobId JobId,
    Guid MachineId,
    string MachineName,
    string Message,
    double ProgressPercent);
