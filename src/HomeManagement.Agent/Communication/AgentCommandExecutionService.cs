using System.Collections.Concurrent;
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

    public AgentCommandExecutionService(
        CommandDispatcher dispatcher,
        ILogger<AgentCommandExecutionService> logger,
        int maxConcurrentCommands)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _commandSemaphore = new SemaphoreSlim(Math.Max(1, maxConcurrentCommands));
    }

    public async Task ProcessAsync(
        ChannelReader<CommandRequest> requestReader,
        ChannelWriter<AgentMessage> outboundWriter,
        CancellationToken ct)
    {
        var inFlight = new ConcurrentDictionary<string, Task>();

        try
        {
            await foreach (var request in requestReader.ReadAllAsync(ct))
            {
                await _commandSemaphore.WaitAsync(ct);
                string requestId = string.IsNullOrWhiteSpace(request.RequestId)
                    ? Guid.NewGuid().ToString("N")
                    : request.RequestId!;

                var executionTask = ExecuteCommandAsync(request, outboundWriter, ct);
                inFlight[requestId] = executionTask;

                _ = executionTask.ContinueWith(
                    _ =>
                    {
                        inFlight.TryRemove(requestId, out var _);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Command execution loop cancelled");
        }

        await Task.WhenAll(inFlight.Values);
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