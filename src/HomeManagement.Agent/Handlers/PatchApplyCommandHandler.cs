using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Handlers;

/// <summary>
/// Routes "PatchApply" commands to <see cref="PatchCommandHandler"/>.
/// Exists because the dispatcher maps one handler per command type,
/// while patching uses a single implementation for both scan and apply.
/// </summary>
public sealed class PatchApplyCommandHandler(PatchCommandHandler inner) : ICommandHandler
{
    public string CommandType => "PatchApply";

    public Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken ct)
        => inner.HandleAsync(request, ct);
}
