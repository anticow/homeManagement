using System.Text.Json;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Agent.Configuration;
using HomeManagement.Agent.Handlers;
using HomeManagement.Agent.Protocol;
using HomeManagement.Agent.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomeManagement.Agent.Tests.Handlers;

public sealed class ManagedServicesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hm-services-{Guid.NewGuid():N}.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private AgentConfiguration Config => new()
    {
        ApiKey = "test-key", UseTls = true, ManagedServicesOnly = true, ManagedServicesPath = _path
    };

    private void Write(params ManagedServiceEnrollment[] services) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(new ManagedServiceManifest(1, services), JsonOptions));

    [Fact]
    public void UnconfiguredNode_HasNoEnrollment()
    {
        new ManagedServiceRegistry(Options.Create(new AgentConfiguration())).Read().Should().BeEmpty();
    }

    [Fact]
    public void ConfiguredMissingOrCorruptManifest_FailsClosed()
    {
        var registry = new ManagedServiceRegistry(Options.Create(Config));
        var read = () => registry.Read();
        read.Should().Throw<IOException>();
        File.WriteAllText(_path, "{");
        read.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("--bad")]
    [InlineData("svc;shutdown")]
    [InlineData("../svc")]
    public void InvalidServiceNames_AreRejected(string name)
    {
        Write(new ManagedServiceEnrollment("app", "Application", name));
        var read = () => new ManagedServiceRegistry(Options.Create(Config)).Read();
        read.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DuplicateAndUnknownSchema_AreRejected()
    {
        Write(new("app", "Application", "HMapp"), new("APP", "Other", "HMother"));
        var read = () => new ManagedServiceRegistry(Options.Create(Config)).Read();
        read.Should().Throw<InvalidOperationException>();
        File.WriteAllText(_path, """{"schemaVersion":2,"services":[]}""");
        read.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Start_UsesOnlyLocallyEnrolledServiceName()
    {
        Write(new ManagedServiceEnrollment("app", "Application", "HMapp"));
        var supervisor = Substitute.For<IManagedServiceSupervisor>();
        supervisor.GetStateAsync("HMapp", Arg.Any<CancellationToken>()).Returns(ServiceState.Running);
        var handler = new ManagedServicesHandler(new(Options.Create(Config)), supervisor,
            NullLogger<ManagedServicesHandler>.Instance);
        var response = await handler.HandleAsync(Request(new("app", ManagedServiceAction.Start)), CancellationToken.None);
        await supervisor.Received(1).ControlAsync("HMapp", ManagedServiceAction.Start, Arg.Any<CancellationToken>());
        response.ExitCode.Should().Be(0);
        JsonSerializer.Deserialize<List<ManagedServiceSnapshot>>(response.Stdout, JsonOptions)![0].State.Should().Be(ServiceState.Running);
    }

    [Fact]
    public async Task WithdrawnEnrollment_IsReloadedAndCannotBeControlled()
    {
        Write(new ManagedServiceEnrollment("app", "Application", "HMapp"));
        var supervisor = Substitute.For<IManagedServiceSupervisor>();
        var handler = new ManagedServicesHandler(new(Options.Create(Config)), supervisor,
            NullLogger<ManagedServicesHandler>.Instance);
        Write();
        var act = () => handler.HandleAsync(Request(new("app", ManagedServiceAction.Stop)), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not enrolled*");
        await supervisor.DidNotReceiveWithAnyArgs().ControlAsync(default!, default, default);
    }

    [Theory]
    [InlineData("""{"id":"app","action":99}""")]
    [InlineData("""{"id":"app"}""")]
    [InlineData("""{"action":0}""")]
    [InlineData("""{"id":"app","action":0,"executable":"evil.exe"}""")]
    public async Task UnsupportedActionsAndLaunchParameters_AreRejected(string payload)
    {
        Write(new ManagedServiceEnrollment("app", "Application", "HMapp"));
        var supervisor = Substitute.For<IManagedServiceSupervisor>();
        var handler = new ManagedServicesHandler(new(Options.Create(Config)), supervisor,
            NullLogger<ManagedServicesHandler>.Instance);
        var act = () => handler.HandleAsync(new() { CommandText = payload }, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
        await supervisor.DidNotReceiveWithAnyArgs().ControlAsync(default!, default, default);
    }

    [Fact]
    public void LocalBreakAndRestrictedMode_BlockLegacyCommandBypasses()
    {
        var validator = new CommandValidator(Options.Create(Config), NullLogger<CommandValidator>.Instance);
        validator.Validate("Shell", "whoami", "None").IsAllowed.Should().BeFalse();
        validator.Validate("ServiceControl", "", "None").IsAllowed.Should().BeFalse();
        validator.Validate("PatchApply", "", "None").IsAllowed.Should().BeFalse();
        validator.Validate("ManagedServices", "", "None").IsAllowed.Should().BeTrue();
        File.WriteAllText(_path + ".disabled", "");
        validator.Validate("ManagedServices", "", "None").IsAllowed.Should().BeFalse();
        var read = () => new ManagedServiceRegistry(Options.Create(Config)).Read();
        read.Should().Throw<InvalidOperationException>().WithMessage("*disabled locally*");
    }

    [Fact]
    public void PlaintextEnrollment_IsRejectedEvenOnLoopback()
    {
        var config = Config;
        config.UseTls = false;
        var validate = () => config.Validate();
        validate.Should().Throw<InvalidOperationException>().WithMessage("*enrollment requires TLS*");
    }

    private static CommandRequest Request(ManagedServiceRequest control) => new()
    {
        RequestId = "test", CommandType = "ManagedServices",
        CommandText = JsonSerializer.Serialize(control, JsonOptions)
    };

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".disabled");
    }
}
