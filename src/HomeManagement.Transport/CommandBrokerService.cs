using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Transport;

/// <summary>
/// Background service that queues command dispatch requests, executes them asynchronously
/// via the agent transport, and persists results to the data store regardless of UI state.
/// Ensures that navigating away from a page does not abandon in-flight commands.
/// </summary>
public sealed class CommandBrokerService : ICommandBroker, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CommandBrokerService> _logger;
    private readonly Subject<CommandCompletedEvent> _completedSubject = new();

    private readonly Channel<QueuedCommand> _queue = Channel.CreateBounded<QueuedCommand>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });

    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;

    public IObservable<CommandCompletedEvent> CompletedStream => _completedSubject.AsObservable();

    public CommandBrokerService(
        IServiceScopeFactory scopeFactory,
        ILogger<CommandBrokerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Start the background processing loop. Call once at application startup.
    /// </summary>
    public void Start()
    {
        _processingTask = Task.Run(() => ProcessLoopAsync(_cts.Token));
        _logger.LogInformation("Command broker started");
    }

    public async Task<Guid> SubmitAsync(CommandEnvelope envelope, CancellationToken ct = default)
    {
        var trackingId = Guid.NewGuid();

        var queued = new QueuedCommand(trackingId, envelope, DateTime.UtcNow);

        await _queue.Writer.WriteAsync(queued, ct);

        _logger.LogInformation("Command {TrackingId} queued for {Machine} ({Description})",
            trackingId, envelope.MachineName, envelope.Description ?? envelope.Command.CommandText[..Math.Min(60, envelope.Command.CommandText.Length)]);

        return trackingId;
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        await foreach (var queued in _queue.Reader.ReadAllAsync(ct))
        {
            await ProcessCommandAsync(queued, ct);
        }
    }

    private async Task ProcessCommandAsync(QueuedCommand queued, CancellationToken ct)
    {
        var envelope = queued.Envelope;
        RemoteResult result;

        try
        {
            _logger.LogDebug("Executing command {TrackingId} on {Machine}",
                queued.TrackingId, envelope.MachineName);

            // Resolve a scoped IRemoteExecutor so the command flows through the
            // full transport pipeline (resilience, routing, agent bridge).
            using var scope = _scopeFactory.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<IRemoteExecutor>();
            result = await executor.ExecuteAsync(envelope.Target, envelope.Command, ct);

            _logger.LogInformation("Command {TrackingId} completed on {Machine}: exit={ExitCode}",
                queued.TrackingId, envelope.MachineName, result.ExitCode);
        }
#pragma warning disable CA1031 // Must capture all failures to persist them
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Command {TrackingId} failed on {Machine}",
                queued.TrackingId, envelope.MachineName);

            result = new RemoteResult(
                ExitCode: -1,
                Stdout: string.Empty,
                Stderr: ex.Message,
                Duration: DateTime.UtcNow - queued.QueuedUtc,
                TimedOut: false);
        }

        // Persist result to the job store if this command is part of a job
        if (envelope.JobId.HasValue)
        {
            await PersistJobResultAsync(envelope.JobId.Value, envelope.MachineId,
                envelope.MachineName, result, ct);
        }

        // Notify subscribers (ViewModels that are still listening)
        var completedEvent = new CommandCompletedEvent(
            queued.TrackingId, envelope.MachineId, envelope.MachineName,
            envelope.JobId, result, DateTime.UtcNow);

        _completedSubject.OnNext(completedEvent);
    }

    private async Task PersistJobResultAsync(
        Guid jobId, Guid machineId, string machineName,
        RemoteResult result, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();

            var machineResult = new JobMachineResult(
                machineId, machineName,
                Success: result.ExitCode == 0,
                ErrorMessage: result.ExitCode != 0 ? result.Stderr : null,
                Duration: result.Duration);

            await jobRepo.AddMachineResultAsync(jobId, machineResult, ct);

            // Update job aggregate counters
            var job = await jobRepo.GetByIdAsync(jobId, ct);
            if (job is not null)
            {
                var newCompleted = job.CompletedTargets + 1;
                var newFailed = result.ExitCode != 0 ? job.FailedTargets + 1 : job.FailedTargets;
                var isFinished = newCompleted >= job.TotalTargets;

                var updated = job with
                {
                    CompletedTargets = newCompleted,
                    FailedTargets = newFailed,
                    State = isFinished
                        ? (newFailed > 0 ? Abstractions.JobState.Failed : Abstractions.JobState.Completed)
                        : Abstractions.JobState.Running,
                    CompletedUtc = isFinished ? DateTime.UtcNow : null
                };

                await jobRepo.UpdateAsync(updated, ct);
            }

            await jobRepo.SaveChangesAsync(ct);
        }
#pragma warning disable CA1031 // Result persistence must not crash the processing loop
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Failed to persist result for job {JobId}, machine {Machine}",
                jobId, machineName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _cts.CancelAsync();

        if (_processingTask is not null)
        {
            try { await _processingTask; }
            catch (OperationCanceledException) { /* expected */ }
        }

        _cts.Dispose();
        _completedSubject.OnCompleted();
        _completedSubject.Dispose();
    }

    private sealed record QueuedCommand(Guid TrackingId, CommandEnvelope Envelope, DateTime QueuedUtc);
}
