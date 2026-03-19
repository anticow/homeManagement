using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Coordinates multi-step operations across machines, manages scheduling and execution.
/// </summary>
public interface IJobScheduler
{
    Task<JobId> SubmitAsync(JobDefinition job, CancellationToken ct = default);
    Task<ScheduleId> ScheduleAsync(JobDefinition job, string cronExpression, CancellationToken ct = default);
    Task CancelAsync(JobId jobId, CancellationToken ct = default);
    Task UnscheduleAsync(ScheduleId scheduleId, CancellationToken ct = default);
    Task<JobStatus> GetStatusAsync(JobId jobId, CancellationToken ct = default);
    Task<PagedResult<JobSummary>> ListJobsAsync(JobQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledJobSummary>> ListSchedulesAsync(CancellationToken ct = default);

    /// <summary>Global progress stream for all jobs.</summary>
    IObservable<JobProgressEvent> ProgressStream { get; }

    /// <summary>Subscribe to progress events for a specific job only.</summary>
    IObservable<JobProgressEvent> GetJobProgressStream(JobId jobId);
}
