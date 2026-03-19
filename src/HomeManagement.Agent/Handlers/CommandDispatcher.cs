using HomeManagement.Agent.Protocol;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Handlers;

/// <summary>
/// Routes inbound <see cref="CommandRequest"/> messages to the appropriate
/// <see cref="ICommandHandler"/> by command type.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly Dictionary<string, ICommandHandler> _handlers;
    private readonly CommandValidator _validator;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(
        IEnumerable<ICommandHandler> handlers,
        CommandValidator validator,
        ILogger<CommandDispatcher> logger)
    {
        _handlers = handlers.ToDictionary(h => h.CommandType, StringComparer.OrdinalIgnoreCase);
        _validator = validator;
        _logger = logger;
    }

    public async Task<CommandResponse> DispatchAsync(CommandRequest request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Validate before dispatch
        var validation = _validator.Validate(request.CommandType, request.CommandText, request.ElevationMode);
        if (!validation.IsAllowed)
        {
            _logger.LogWarning("Command {RequestId} rejected: {Reason}",
                request.RequestId, validation.ErrorMessage);

            return new CommandResponse
            {
                RequestId = request.RequestId,
                ExitCode = -1,
                Stderr = validation.ErrorMessage ?? "Rejected",
                ErrorCategory = validation.ErrorCategory ?? "Authorization",
                DurationMs = sw.ElapsedMilliseconds,
                CorrelationId = request.CorrelationId
            };
        }

        if (!_handlers.TryGetValue(request.CommandType, out var handler))
        {
            _logger.LogWarning("No handler for command type '{CommandType}'", request.CommandType);
            return new CommandResponse
            {
                RequestId = request.RequestId,
                ExitCode = -1,
                Stderr = $"Unknown command type: {request.CommandType}",
                ErrorCategory = "ConfigurationError",
                DurationMs = sw.ElapsedMilliseconds,
                CorrelationId = request.CorrelationId
            };
        }

        try
        {
            // Enforce per-command timeout
            using var timeoutCts = request.TimeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds))
                : new CancellationTokenSource();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var response = await handler.HandleAsync(request, linkedCts.Token);
            response.DurationMs = sw.ElapsedMilliseconds;
            response.CorrelationId = request.CorrelationId;
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Command {RequestId} timed out after {Timeout}s",
                request.RequestId, request.TimeoutSeconds);

            return new CommandResponse
            {
                RequestId = request.RequestId,
                ExitCode = -1,
                TimedOut = true,
                Stderr = $"Command timed out after {request.TimeoutSeconds}s",
                ErrorCategory = "Transient",
                DurationMs = sw.ElapsedMilliseconds,
                CorrelationId = request.CorrelationId
            };
        }
#pragma warning disable CA1031 // Handler failures should not crash the agent
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Command {RequestId} failed with exception", request.RequestId);

            return new CommandResponse
            {
                RequestId = request.RequestId,
                ExitCode = -1,
                Stderr = ex.Message,
                ErrorCategory = "SystemError",
                DurationMs = sw.ElapsedMilliseconds,
                CorrelationId = request.CorrelationId
            };
        }
    }
}
