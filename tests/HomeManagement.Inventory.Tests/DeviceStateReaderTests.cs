using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomeManagement.Inventory.Tests;

public sealed class DeviceStateReaderTests
{
    [Fact]
    public void DefaultsAreFiveMinuteValidationAndTwentyFiveMinuteStaleness()
    {
        var options = new DeviceStateOptions();
        options.ValidationIntervalSeconds.Should().Be(300);
        options.StaleAfterSeconds.Should().Be(1500);
    }

    [Theory]
    [InlineData(1499, false, DeviceConnectivity.Online)]
    [InlineData(1500, true, DeviceConnectivity.Unknown)]
    [InlineData(1501, true, DeviceConnectivity.Unknown)]
    public async Task FreshnessIsRecomputedFromLastSuccessAtExactBoundary(int age, bool stale, DeviceConnectivity expected)
    {
        var clock = new Clock();
        var machine = Machine();
        var inventory = Substitute.For<IMachineRepository>();
        inventory.QueryAsync(Arg.Any<MachineQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Machine>([machine], 1, 1, 25));
        var stored = new DeviceStateSnapshot
        {
            MachineId = machine.Id,
            LastKnownConnected = true,
            AgentSuccessUtc = clock.Now,
            AgentAttemptUtc = clock.Now.AddSeconds(age),
            AgentFailure = "Gateway validation unavailable.",
            MetricsSuccessUtc = clock.Now
        };
        var states = Substitute.For<IDeviceStateRepository>();
        states.GetForMachinesAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DeviceStateSnapshot> { [machine.Id] = stored });
        var reader = new DeviceStateReader(inventory, states, Options.Create(new DeviceStateOptions()), clock);
        clock.Now = clock.Now.AddSeconds(age);
        var device = (await reader.QueryAsync(new MachineQuery())).Items.Single();
        device.Connectivity.Should().Be(expected);
        device.AgentIsStale.Should().Be(stale);
        device.MetricsIsStale.Should().Be(stale);
        device.AgentAgeSeconds.Should().Be(age);
        device.Snapshot.LastKnownConnected.Should().BeTrue();
        device.Snapshot.AgentFailure.Should().NotBeNull();
    }

    [Fact]
    public async Task NeverValidatedIsUnknownNonAgentIsNotApplicableAndDeletedIsExcluded()
    {
        var machine = Machine();
        var nonAgent = Machine() with { Protocol = TransportProtocol.Ssh };
        var deleted = Machine() with { IsDeleted = true };
        var inventory = Substitute.For<IMachineRepository>();
        inventory.QueryAsync(Arg.Any<MachineQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Machine>([machine, nonAgent, deleted], 3, 1, 25));
        var states = Substitute.For<IDeviceStateRepository>();
        states.GetForMachinesAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, DeviceStateSnapshot>());
        var reader = new DeviceStateReader(inventory, states, Options.Create(new DeviceStateOptions()), new Clock());
        var result = await reader.QueryAsync(new MachineQuery(IncludeDeleted: true));
        result.Items.Should().HaveCount(2);
        result.Items[0].Connectivity.Should().Be(DeviceConnectivity.Unknown);
        result.Items[0].AgentAgeSeconds.Should().BeNull();
        result.Items[0].AgentIsStale.Should().BeTrue();
        result.Items[1].Connectivity.Should().Be(DeviceConnectivity.NotApplicable);
        result.Items[1].AgentIsStale.Should().BeFalse();
        await inventory.Received(1).QueryAsync(Arg.Is<MachineQuery>(q => !q.IncludeDeleted), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuthoritativeDisconnectedSuccessIsOffline()
    {
        var clock = new Clock();
        var machine = Machine();
        var inventory = Substitute.For<IMachineRepository>();
        inventory.QueryAsync(Arg.Any<MachineQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Machine>([machine], 1, 1, 25));
        var states = Substitute.For<IDeviceStateRepository>();
        states.GetForMachinesAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>()).Returns(
            new Dictionary<Guid, DeviceStateSnapshot>
            {
                [machine.Id] = new() { MachineId = machine.Id, AgentSuccessUtc = clock.Now, LastKnownConnected = false }
            });
        var reader = new DeviceStateReader(inventory, states, Options.Create(new DeviceStateOptions()), clock);
        (await reader.QueryAsync(new MachineQuery())).Items.Single().Connectivity.Should().Be(DeviceConnectivity.Offline);
    }

    private static Machine Machine() => new(Guid.NewGuid(), Hostname.Create("node"), null, [], OsType.Linux, "Linux",
        MachineConnectionMode.Agent, TransportProtocol.Agent, 0, Guid.Empty, MachineState.Offline,
        new Dictionary<string, string>(), null, default, default, default);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 16, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
