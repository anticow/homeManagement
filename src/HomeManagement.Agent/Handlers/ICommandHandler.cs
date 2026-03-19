using HomeManagement.Agent.Protocol;

namespace HomeManagement.Agent.Handlers;

/// <summary>
/// Handles a specific command type received from the controller.
/// </summary>
public interface ICommandHandler
{
    string CommandType { get; }
    Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken ct);
}
