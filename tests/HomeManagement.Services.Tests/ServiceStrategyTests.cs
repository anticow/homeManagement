using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Services.Tests;

public sealed class LinuxServiceStrategyTests
{
    private readonly LinuxServiceStrategy _sut = new();

    [Fact]
    public void BuildStatusCommand_ContainsServiceName()
    {
        var name = ServiceName.Create("nginx");
        var cmd = _sut.BuildStatusCommand(name);
        cmd.Should().Contain("systemctl show nginx");
    }

    [Fact]
    public void ParseStatusOutput_ParsesActiveService()
    {
        var output = "ActiveState=active\nSubState=running\nMainPID=1234\nLoadState=loaded\nUnitFileState=enabled\nDescription=Nginx Web Server";
        var name = ServiceName.Create("nginx");

        var result = _sut.ParseStatusOutput(output, name);

        result.State.Should().Be(ServiceState.Running);
        result.StartupType.Should().Be(ServiceStartupType.Automatic);
        result.ProcessId.Should().Be(1234);
        result.DisplayName.Should().Contain("Nginx");
    }

    [Fact]
    public void ParseStatusOutput_ParsesStoppedService()
    {
        var output = "ActiveState=inactive\nSubState=dead\nMainPID=0\nUnitFileState=disabled";
        var name = ServiceName.Create("nginx");

        var result = _sut.ParseStatusOutput(output, name);

        result.State.Should().Be(ServiceState.Stopped);
        result.StartupType.Should().Be(ServiceStartupType.Disabled);
    }

    [Fact]
    public void ParseListOutput_ParsesMultipleServices()
    {
        var output = "nginx.service loaded active running A high performance web server\nsshd.service loaded active running OpenBSD Secure Shell server";

        var result = _sut.ParseListOutput(output);

        result.Should().HaveCount(2);
        result[0].Name.Value.Should().Be("nginx");
        result[0].State.Should().Be(ServiceState.Running);
        result[1].Name.Value.Should().Be("sshd");
    }

    [Fact]
    public void ParseListOutput_EmptyInput_ReturnsEmpty()
    {
        _sut.ParseListOutput("").Should().BeEmpty();
    }

    [Fact]
    public void BuildControlCommand_GeneratesCorrectVerbs()
    {
        var name = ServiceName.Create("nginx");

        _sut.BuildControlCommand(name, ServiceAction.Start).Should().Contain("start");
        _sut.BuildControlCommand(name, ServiceAction.Stop).Should().Contain("stop");
        _sut.BuildControlCommand(name, ServiceAction.Restart).Should().Contain("restart");
        _sut.BuildControlCommand(name, ServiceAction.Enable).Should().Contain("enable");
        _sut.BuildControlCommand(name, ServiceAction.Disable).Should().Contain("disable");
    }

    [Fact]
    public void BuildListCommand_WithRunningFilter_IncludesStateFilter()
    {
        var filter = new ServiceFilter(State: ServiceState.Running);
        _sut.BuildListCommand(filter).Should().Contain("--state=running");
    }

    [Fact]
    public void BuildListCommand_NoFilter_DoesNotIncludeStateFilter()
    {
        _sut.BuildListCommand(null).Should().NotContain("--state=");
    }
}

public sealed class WindowsServiceStrategyTests
{
    private readonly WindowsServiceStrategy _sut = new();

    [Fact]
    public void ParseStatusOutput_ParsesSingleJsonObject()
    {
        var json = "{\"Name\":\"wuauserv\",\"DisplayName\":\"Windows Update\",\"Status\":\"Running\",\"StartType\":\"Automatic\"}";
        var name = ServiceName.Create("wuauserv");

        var result = _sut.ParseStatusOutput(json, name);

        result.State.Should().Be(ServiceState.Running);
        result.StartupType.Should().Be(ServiceStartupType.Automatic);
        result.DisplayName.Should().Be("Windows Update");
    }

    [Fact]
    public void ParseStatusOutput_InvalidJson_ReturnsUnknown()
    {
        var name = ServiceName.Create("wuauserv");
        var result = _sut.ParseStatusOutput("not json", name);

        result.State.Should().Be(ServiceState.Unknown);
    }

    [Fact]
    public void ParseStatusOutput_EmptyInput_ReturnsUnknown()
    {
        var name = ServiceName.Create("wuauserv");
        var result = _sut.ParseStatusOutput("", name);

        result.State.Should().Be(ServiceState.Unknown);
    }

    [Fact]
    public void ParseListOutput_ParsesJsonArray()
    {
        var json = "[{\"Name\":\"wuauserv\",\"DisplayName\":\"Windows Update\",\"Status\":\"Running\",\"StartType\":\"Automatic\"},{\"Name\":\"spooler\",\"DisplayName\":\"Print Spooler\",\"Status\":\"Stopped\",\"StartType\":\"Manual\"}]";

        var result = _sut.ParseListOutput(json);

        result.Should().HaveCount(2);
        result[0].State.Should().Be(ServiceState.Running);
        result[1].State.Should().Be(ServiceState.Stopped);
    }

    [Fact]
    public void ParseListOutput_EmptyInput_ReturnsEmpty()
    {
        _sut.ParseListOutput("").Should().BeEmpty();
    }

    [Fact]
    public void BuildControlCommand_GeneratesCorrectCommands()
    {
        var name = ServiceName.Create("wuauserv");

        _sut.BuildControlCommand(name, ServiceAction.Start).Should().Contain("Start-Service");
        _sut.BuildControlCommand(name, ServiceAction.Stop).Should().Contain("Stop-Service");
        _sut.BuildControlCommand(name, ServiceAction.Restart).Should().Contain("Restart-Service");
        _sut.BuildControlCommand(name, ServiceAction.Enable).Should().Contain("StartupType Automatic");
        _sut.BuildControlCommand(name, ServiceAction.Disable).Should().Contain("StartupType Disabled");
    }

    [Fact]
    public void ParseStatusOutput_HandlesNumericEnumValues()
    {
        var json = "{\"Name\":\"wuauserv\",\"DisplayName\":\"Windows Update\",\"Status\":4,\"StartType\":2}";
        var name = ServiceName.Create("wuauserv");

        var result = _sut.ParseStatusOutput(json, name);

        result.State.Should().Be(ServiceState.Running);
        result.StartupType.Should().Be(ServiceStartupType.Automatic);
    }
}
