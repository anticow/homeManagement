using System.Threading.Channels;
using FluentAssertions;
using HomeManagement.Agent.Communication;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Protocol;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeManagement.Agent.Tests.Communication;

public sealed class AgentCommandExecutionServiceTests
{
    [Fact]
    public async Task ProcessAsync_RespectsConfiguredConcurrencyLimit()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingCommandHandler(gate.Task);
        var dispatcher = new CommandDispatcher(
            [handler],
            new CommandValidator(
                Options.Create(new AgentConfiguration
                {
                    CommandRateLimit = 100,
                    AllowElevation = false,
                    DeniedCommandPatterns = []
                }),
                NullLogger<CommandValidator>.Instance),
            NullLogger<CommandDispatcher>.Instance);

        var service = new AgentCommandExecutionService(
            dispatcher,
            NullLogger<AgentCommandExecutionService>.Instance,
            maxConcurrentCommands: 2);

        var inbound = Channel.CreateUnbounded<CommandRequest>();
        var outbound = Channel.CreateUnbounded<AgentMessage>();

        var processingTask = service.ProcessAsync(inbound.Reader, outbound.Writer, CancellationToken.None);

        for (var index = 1; index <= 3; index++)
        {
            await inbound.Writer.WriteAsync(new CommandRequest
            {
                RequestId = $"req-{index}",
                CommandType = "Shell",
                CommandText = "echo test",
                ElevationMode = "None"
            });
        }

        inbound.Writer.Complete();

        await WaitUntilAsync(() => handler.CurrentExecutions == 2);
        await Task.Delay(100);

        handler.StartedExecutions.Should().Be(2);
        handler.MaxConcurrentExecutions.Should().Be(2);

        gate.SetResult();
        await processingTask;

        var responses = new List<AgentMessage>();
        while (outbound.Reader.TryRead(out var message))
        {
            responses.Add(message);
        }

        responses.Should().HaveCount(3);
        handler.MaxConcurrentExecutions.Should().Be(2);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!predicate())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class BlockingCommandHandler : ICommandHandler
    {
        private readonly Task _gateTask;
        private int _currentExecutions;
        private int _startedExecutions;
        private int _maxConcurrentExecutions;

        public BlockingCommandHandler(Task gateTask)
        {
            _gateTask = gateTask;
        }

        public string CommandType => "Shell";

        public int CurrentExecutions => Volatile.Read(ref _currentExecutions);

        public int StartedExecutions => Volatile.Read(ref _startedExecutions);

        public int MaxConcurrentExecutions => Volatile.Read(ref _maxConcurrentExecutions);

        public async Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _startedExecutions);

            var current = Interlocked.Increment(ref _currentExecutions);
            UpdateMaximum(current);

            try
            {
                await _gateTask.WaitAsync(ct);
                return new CommandResponse
                {
                    RequestId = request.RequestId,
                    ExitCode = 0,
                    Stdout = "ok"
                };
            }
            finally
            {
                Interlocked.Decrement(ref _currentExecutions);
            }
        }

        private void UpdateMaximum(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxConcurrentExecutions);
                if (current <= observed)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxConcurrentExecutions, current, observed) == observed)
                {
                    return;
                }
            }
        }
    }
}
