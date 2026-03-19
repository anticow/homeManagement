using FluentAssertions;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeManagement.Agent.Tests.Handlers;

/// <summary>
/// Tests for <see cref="PatchApplyCommandHandler"/> — verifies routing gap fix (NEW-03).
/// PatchCommandHandler is sealed, so we test with a real instance.
/// </summary>
public sealed class PatchApplyCommandHandlerTests
{
    [Fact]
    public void CommandType_ReturnsPatchApply()
    {
        var inner = new PatchCommandHandler(NullLogger<PatchCommandHandler>.Instance);
        var handler = new PatchApplyCommandHandler(inner);
        handler.CommandType.Should().Be("PatchApply");
    }

    [Fact]
    public async Task HandleAsync_DelegatesToInnerHandler_ReturnsMatchingRequestId()
    {
        var inner = new PatchCommandHandler(NullLogger<PatchCommandHandler>.Instance);
        var handler = new PatchApplyCommandHandler(inner);
        var request = new CommandRequest
        {
            RequestId = "r1",
            CommandType = "PatchApply",
            ParametersJson = """{"patchIds":["KB5001234"]}""",
            ElevationMode = "None"
        };

        // The inner handler will attempt to run a process which will fail in test,
        // but the response structure should still carry the RequestId through.
        var result = await handler.HandleAsync(request, CancellationToken.None);

        result.RequestId.Should().Be("r1");
    }
}
