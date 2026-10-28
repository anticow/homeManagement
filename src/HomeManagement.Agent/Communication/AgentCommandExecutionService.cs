using System.Threading.Channels;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Communication;

public sealed class AgentCommandExecutionService : IDisposable
{
    private readonly CommandDispatcher _dispatcher;
    private readonly ILogger<AgentCommandExecutionService> _logger;
    private readonly SemaphoreSlim _commandSemaphore;
    private readonly int _maxConcurrentCommands;

    public AgentCommandExecutionService(
        CommandDispatcher dispatcher,
        ILogger<AgentCommandExecutionService> logger,
        int maxConcurrentCommands)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentCommands);
        _maxConcurrentCommands = maxConcurrentCommands;
        _commandSemaphore = new SemaphoreSlim(maxConcurrentCommands);
    }

    public async Task ProcessAsync(
        ChannelReader<CommandRequest> requestReader,
        ChannelWriter<AgentMessage> outboundWriter,
        CancellationToken ct)
    {
        var inFlight = new List<Task>();

        try
        {
            await foreach (var request in requestReader.ReadAllAsync(ct))
            {
                await _commandSemaphore.WaitAsync(ct);
                var executionTask = ExecuteCommandAsync(request, outboundWriter, ct);
                inFlight.Add(executionTask);
                if (inFlight.Count >= _maxConcurrentCommands)
                {
                    await await Task.WhenAny(inFlight);
                    inFlight.RemoveAll(task => task.IsCompletedSuccessfully);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Command execution loop cancelled");
        }

        finally
        {
            await Task.WhenAll(inFlight);
        }
    }

    private async Task ExecuteCommandAsync(
        CommandRequest request,
        ChannelWriter<AgentMessage> outboundWriter,
        CancellationToken ct)
    {
        try
        {
            var response = await _dispatcher.DispatchAsync(request, ct);
            await outboundWriter.WriteAsync(new AgentMessage { CommandResponse = response }, ct);
        }
        finally
        {
            _commandSemaphore.Release();
        }
    }

    public void Dispose()
    {
        _commandSemaphore.Dispose();
    }
}
