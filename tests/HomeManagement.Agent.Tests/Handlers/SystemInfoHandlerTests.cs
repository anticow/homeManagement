using FluentAssertions;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace HomeManagement.Agent.Tests.Handlers;

public class SystemInfoHandlerTests
{
    private readonly SystemInfoHandler _sut = new(Substitute.For<ILogger<SystemInfoHandler>>());

    [Fact]
    public void CommandType_ReturnsSystemInfo()
    {
        _sut.CommandType.Should().Be("SystemInfo");
    }

    [Fact]
    public async Task HandleAsync_ReturnsZeroExitCode()
    {
        var request = new CommandRequest { RequestId = "req-1", CommandType = "SystemInfo" };

        var result = await _sut.HandleAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.RequestId.Should().Be("req-1");
    }

    [Fact]
    public async Task HandleAsync_ReturnsJsonWithHostname()
    {
        var request = new CommandRequest { RequestId = "req-2", CommandType = "SystemInfo" };

        var result = await _sut.HandleAsync(request, CancellationToken.None);

        result.ResultJson.Should().NotBeNullOrEmpty();
        result.ResultJson.Should().Contain("hostname");
        result.ResultJson.Should().Contain(Environment.MachineName);
    }

    [Fact]
    public async Task HandleAsync_ReturnsJsonWithOsType()
    {
        var request = new CommandRequest { RequestId = "req-3", CommandType = "SystemInfo" };

        var result = await _sut.HandleAsync(request, CancellationToken.None);

        result.ResultJson.Should().Contain("osType");
    }

    [Fact]
    public async Task HandleAsync_ReturnsJsonWithProcessorCount()
    {
        var request = new CommandRequest { RequestId = "req-4", CommandType = "SystemInfo" };

        var result = await _sut.HandleAsync(request, CancellationToken.None);

        result.ResultJson.Should().Contain("processorCount");
    }

    [Fact]
    public async Task HandleAsync_ReturnsJsonWithDiskInfo()
    {
        var request = new CommandRequest { RequestId = "req-5", CommandType = "SystemInfo" };

        var result = await _sut.HandleAsync(request, CancellationToken.None);

        result.ResultJson.Should().Contain("disks");
    }

    [Fact]
    public async Task HandleAsync_ResultJsonIsValidJson()
    {
        var request = new CommandRequest { RequestId = "req-6", CommandType = "SystemInfo" };

        var result = await _sut.HandleAsync(request, CancellationToken.None);

        var act = () => System.Text.Json.JsonDocument.Parse(result.ResultJson!);
        act.Should().NotThrow();
    }
}
