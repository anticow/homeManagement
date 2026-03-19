namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Fire-and-forget command broker that queues operations, executes them asynchronously
/// via the agent transport, and persists results to the data store regardless of UI state.
/// </summary>
public interface ICommandBroker
{
    /// <summary>
    /// Submit a command for asynchronous execution. Returns immediately with a tracking ID.
    /// Results are written to the data store when they arrive.
    /// </summary>
    Task<Guid> SubmitAsync(CommandEnvelope envelope, CancellationToken ct = default);

    /// <summary>
    /// Observable stream of completed command results, for UI reactive updates.
    /// </summary>
    IObservable<CommandCompletedEvent> CompletedStream { get; }
}

/// <summary>
/// Wraps a command to be dispatched to an agent, with all the context needed
/// to persist the result after completion.
/// </summary>
public record CommandEnvelope(
    Guid MachineId,
    string MachineName,
    Models.MachineTarget Target,
    Models.RemoteCommand Command,
    Guid? JobId = null,
    string? Description = null);

/// <summary>
/// Fired when an async command completes (success or failure).
/// </summary>
public record CommandCompletedEvent(
    Guid TrackingId,
    Guid MachineId,
    string MachineName,
    Guid? JobId,
    Models.RemoteResult Result,
    DateTime CompletedUtc);
