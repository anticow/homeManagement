using System.Collections.Concurrent;

namespace HomeManagement.Integration.Action1;

/// <summary>Outcome of a single catalog update approval attempt.</summary>
public enum ApprovalOutcome
{
    Success,
    Forbidden,          // 403 — permission problem, do not retry
    RateLimitExhausted, // 429 exhausted all retries — eligible for second-pass retry
    Error,              // non-retriable server/network error
    NotSupported        // API does not support this operation for this package type (e.g. named _builtin software delivery packages)
}

/// <summary>Snapshot of a running or completed bulk approval job.</summary>
public sealed record ApprovalJobStatus(
    string JobId,
    int Total,
    int Processed,
    int Succeeded,
    int Failed,
    int Skipped,
    bool IsComplete,
    IReadOnlyList<string> FailedIds,
    IReadOnlyList<string> SkippedIds);

/// <summary>
/// Thread-safe in-memory store for background bulk-approval jobs.
/// Registered as a singleton in the broker DI container.
/// Jobs older than 2 hours are evicted on each Create call.
/// </summary>
public sealed class ApprovalJobStore
{
    private sealed class JobState
    {
        public readonly int Total;
        public int Processed;
        public int Succeeded;
        public int Failed;
        public int Skipped;
        public bool IsComplete;
        public readonly ConcurrentBag<string> FailedIds = [];
        public readonly ConcurrentBag<string> SkippedIds = [];
        public readonly DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;

        public JobState(int total) => Total = total;
    }

    private readonly ConcurrentDictionary<string, JobState> _jobs = new();

    public string CreateJob(int total)
    {
        EvictStale();
        var id = Guid.NewGuid().ToString("N")[..12]; // short but unique enough
        _jobs[id] = new JobState(total);
        return id;
    }

    public void RecordSuccess(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        Interlocked.Increment(ref job.Processed);
        Interlocked.Increment(ref job.Succeeded);
    }

    public void RecordFailure(string jobId, string itemId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        Interlocked.Increment(ref job.Processed);
        Interlocked.Increment(ref job.Failed);
        job.FailedIds.Add(itemId);
    }

    public void RecordSkipped(string jobId, string itemId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return;
        Interlocked.Increment(ref job.Processed);
        Interlocked.Increment(ref job.Skipped);
        job.SkippedIds.Add(itemId);
    }

    public void Complete(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
            Volatile.Write(ref job.IsComplete, true);
    }

    public ApprovalJobStatus? GetStatus(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return null;
        return new ApprovalJobStatus(
            jobId,
            job.Total,
            Volatile.Read(ref job.Processed),
            Volatile.Read(ref job.Succeeded),
            Volatile.Read(ref job.Failed),
            Volatile.Read(ref job.Skipped),
            Volatile.Read(ref job.IsComplete),
            job.FailedIds.ToArray(),
            job.SkippedIds.ToArray());
    }

    private void EvictStale()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        foreach (var (key, job) in _jobs)
            if (job.CreatedAt < cutoff)
                _jobs.TryRemove(key, out _);
    }
}
