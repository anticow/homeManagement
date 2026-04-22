using FluentAssertions;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Protocol;
using HomeManagement.Agent.Security;
using HomeManagement.Agent.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HomeManagement.Agent.Tests.Handlers;

public sealed class CommandDispatcherTests
{
    private static CommandDispatcher CreateDispatcher(
        IEnumerable<ICommandHandler>? handlers = null,
        int rateLimit = 100,
        bool allowElevation = false)
    {
        var config = new AgentConfiguration
        {
            CommandRateLimit = rateLimit,
            AllowElevation = allowElevation,
            DeniedCommandPatterns = []
        };

        var validator = new CommandValidator(
            Options.Create(config),
            NullLogger<CommandValidator>.Instance);

        return new CommandDispatcher(
            handlers ?? [],
            validator,
            NullLogger<CommandDispatcher>.Instance);
    }

    private static CommandRequest MakeRequest(string commandType, string? commandText = null, int timeoutSeconds = 0) =>
        new()
        {
            RequestId = Guid.NewGuid().ToString(),
            CommandType = commandType,
            CommandText = commandText ?? "",
            ParametersJson = "{}",
            ElevationMode = "None",
            TimeoutSeconds = timeoutSeconds
        };

    // ── Routing ──

    [Fact]
    public async Task DispatchAsync_KnownHandler_DelegatesToHandler()
    {
        var handler = Substitute.For<ICommandHandler>();
        handler.CommandType.Returns("Shell");
        handler.HandleAsync(Arg.Any<CommandRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResponse { RequestId = "r1", ExitCode = 0, Stdout = "ok" });

        var dispatcher = CreateDispatcher([handler]);
        var request = MakeRequest("Shell", "echo hi");

        var response = await dispatcher.DispatchAsync(request, CancellationToken.None);

        response.ExitCode.Should().Be(0);
        response.Stdout.Should().Be("ok");
        await handler.Received(1).HandleAsync(Arg.Any<CommandRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_UnknownCommandType_ReturnsConfigurationError()
    {
        var dispatcher = CreateDispatcher([]);
        var request = MakeRequest("BogusType");

        var response = await dispatcher.DispatchAsync(request, CancellationToken.None);

        response.ExitCode.Should().Be(-1);
        response.ErrorCategory.Should().Be("Authorization");
        response.Stderr.Should().Contain("Unknown command type");
    }

    [Fact]
    public async Task DispatchAsync_NoHandlerRegistered_ReturnsConfigurationError()
    {
        // "Shell" passes validation but has no handler registered
        var dispatcher = CreateDispatcher([]);
        var request = MakeRequest("Shell", "ls");

        var response = await dispatcher.DispatchAsync(request, CancellationToken.None);

        response.ExitCode.Should().Be(-1);
        response.ErrorCategory.Should().Be("ConfigurationError");
        response.Stderr.Should().Contain("Unknown command type: Shell");
    }

    // ── Validation rejection ──

    [Fact]
    public async Task DispatchAsync_ElevationRejected_DoesNotCallHandler()
    {
        var handler = Substitute.For<ICommandHandler>();
        handler.CommandType.Returns("Shell");

        var dispatcher = CreateDispatcher([handler], allowElevation: false);
        var request = MakeRequest("Shell", "ls");
        request.ElevationMode = "Sudo";

        var response = await dispatcher.DispatchAsync(request, CancellationToken.None);

        response.ExitCode.Should().Be(-1);
        response.ErrorCategory.Should().Be("Authorization");
        await handler.DidNotReceive().HandleAsync(Arg.Any<CommandRequest>(), Arg.Any<CancellationToken>());
    }

    // ── Exception handling ──

    [Fact]
    public async Task DispatchAsync_HandlerThrows_ReturnsSystemError()
    {
        var handler = Substitute.For<ICommandHandler>();
        handler.CommandType.Returns("Shell");
        handler.HandleAsync(Arg.Any<CommandRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("test failure"));

        var dispatcher = CreateDispatcher([handler]);
        var request = MakeRequest("Shell", "echo hi");

        var response = await dispatcher.DispatchAsync(request, CancellationToken.None);

        response.ExitCode.Should().Be(-1);
        response.ErrorCategory.Should().Be("SystemError");
        response.Stderr.Should().Contain("test failure");
    }

    // ── Duration tracking ──

    [Fact]
    public async Task DispatchAsync_SetsDurationMs()
    {
        var handler = Substitute.For<ICommandHandler>();
        handler.CommandType.Returns("Shell");
        handler.HandleAsync(Arg.Any<CommandRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResponse { RequestId = "r1", ExitCode = 0 });

        var dispatcher = CreateDispatcher([handler]);
        var request = MakeRequest("Shell", "ls");

        var response = await dispatcher.DispatchAsync(request, CancellationToken.None);

        response.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    // ── Case-insensitive routing ──

    [Fact]
    public async Task DispatchAsync_CaseInsensitiveHandlerLookup()
    {
        var handler = Substitute.For<ICommandHandler>();
        handler.CommandType.Returns("shell");
        handler.HandleAsync(Arg.Any<CommandRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResponse { RequestId = "r1", ExitCode = 0 });

        var dispatcher = CreateDispatcher([handler]);
        var request = MakeRequest("SHELL", "ls");

        var response = await dispatcher.DispatchAsync(request, CancellationToken.None);

        response.ExitCode.Should().Be(0);
    }
}
