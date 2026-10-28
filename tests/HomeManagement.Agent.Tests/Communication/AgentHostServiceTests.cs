using FluentAssertions;
using Grpc.Core;
using HomeManagement.Agent.Communication;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Protocol;
using HomeManagement.Agent.Resilience;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomeManagement.Agent.Tests.Communication;

public sealed class AgentHostServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunConnectionAsync_StreamFailureStopsAllConnectionWork(bool receiveFails)
    {
        var config = Options.Create(new AgentConfiguration { ApiKey = "test-key", HeartbeatIntervalSeconds = 1 });
        var handler = new TestHandler(receiveFails);
        var dispatcher = new CommandDispatcher(
            [handler],
            new CommandValidator(config, NullLogger<CommandValidator>.Instance),
            NullLogger<CommandDispatcher>.Instance);
        using var execution = new AgentCommandExecutionService(
            dispatcher, NullLogger<AgentCommandExecutionService>.Instance, 1);
        var certificates = new CertificateLoader(config, NullLogger<CertificateLoader>.Instance);
        using var channels = new GrpcChannelManager(config, certificates, NullLogger<GrpcChannelManager>.Instance);
        var shutdown = new ShutdownCoordinator(
            Substitute.For<IHostApplicationLifetime>(), NullLogger<ShutdownCoordinator>.Instance);
        var update = new UpdateCommandHandler(
            new IntegrityChecker(NullLogger<IntegrityChecker>.Instance),
            shutdown, config, NullLogger<UpdateCommandHandler>.Instance);
        using var service = new AgentHostService(
            config, channels, dispatcher, new ReconnectPolicy(NullLogger<ReconnectPolicy>.Instance),
            shutdown, update, execution, NullLogger<AgentHostService>.Instance);
        var reader = new TestReader(handler, receiveFails);
        var client = new TestClient(reader, !receiveFails);

        var action = async () => await service.RunConnectionAsync(client, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(receiveFails ? "receive failed" : "send failed");
        client.ConnectionToken.IsCancellationRequested.Should().BeTrue();
        handler.Stopped.Task.IsCompleted.Should().BeTrue();
        reader.Stopped.Should().BeTrue();
    }

    private sealed class TestClient(TestReader reader, bool sendFails) : AgentHub.AgentHubClient
    {
        public CancellationToken ConnectionToken { get; private set; }

        public override AsyncDuplexStreamingCall<AgentMessage, ControlMessage> Connect(
            Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
        {
            ConnectionToken = cancellationToken;
            return new AsyncDuplexStreamingCall<AgentMessage, ControlMessage>(
                new TestWriter(sendFails), reader, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
    }

    private sealed class TestWriter(bool sendFails) : IClientStreamWriter<AgentMessage>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task CompleteAsync() => Task.CompletedTask;
        public Task WriteAsync(AgentMessage message) =>
            sendFails && message.CommandResponse is not null
                ? Task.FromException(new InvalidOperationException("send failed"))
                : Task.CompletedTask;
        public Task WriteAsync(AgentMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return WriteAsync(message);
        }
    }

    private sealed class TestReader(TestHandler handler, bool receiveFails) : IAsyncStreamReader<ControlMessage>
    {
        private int _reads;
        public bool Stopped { get; private set; }
        public ControlMessage Current { get; } = new()
        {
            CommandRequest = new CommandRequest
            {
                RequestId = "test",
                CommandType = "SystemInfo",
                ElevationMode = "None"
            }
        };

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_reads++ == 0)
                return true;

            try
            {
                await handler.Started.Task.WaitAsync(cancellationToken);
                if (receiveFails)
                    throw new InvalidOperationException("receive failed");

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return false;
            }
            finally
            {
                Stopped = true;
            }
        }
    }

    private sealed class TestHandler(bool waitForCancellation) : ICommandHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CommandType => "SystemInfo";

        public async Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken ct)
        {
            Started.TrySetResult();
            try
            {
                if (waitForCancellation)
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new CommandResponse { RequestId = request.RequestId };
            }
            finally
            {
                Stopped.TrySetResult();
            }
        }
    }
}
