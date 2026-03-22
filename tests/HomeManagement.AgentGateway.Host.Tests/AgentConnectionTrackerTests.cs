using FluentAssertions;
using HomeManagement.AgentGateway.Host.Services;

namespace HomeManagement.AgentGateway.Host.Tests;

/// <summary>
/// Tests for <see cref="AgentConnectionTracker"/> — in-memory agent connection tracking.
/// </summary>
public sealed class AgentConnectionTrackerTests
{
    private static AgentConnectionInfo CreateInfo(string agentId = "agent-1", string hostname = "srv01") =>
        new(agentId, hostname, "Windows", "1.0.0", DateTime.UtcNow);

    // ── Register ──

    [Fact]
    public void Register_NewAgent_IncreasesCount()
    {
        var tracker = new AgentConnectionTracker();

        tracker.Register("agent-1", CreateInfo());

        tracker.Count.Should().Be(1);
    }

    [Fact]
    public void Register_SameAgentTwice_UpdatesInfo()
    {
        var tracker = new AgentConnectionTracker();
        var original = CreateInfo(hostname: "old-host");
        var updated = CreateInfo(hostname: "new-host");

        tracker.Register("agent-1", original);
        tracker.Register("agent-1", updated);

        tracker.Count.Should().Be(1);
        tracker.Get("agent-1")!.Hostname.Should().Be("new-host");
    }

    [Fact]
    public void Register_MultipleAgents_TracksAll()
    {
        var tracker = new AgentConnectionTracker();

        tracker.Register("a1", CreateInfo("a1", "host1"));
        tracker.Register("a2", CreateInfo("a2", "host2"));
        tracker.Register("a3", CreateInfo("a3", "host3"));

        tracker.Count.Should().Be(3);
    }

    // ── Unregister ──

    [Fact]
    public void Unregister_ExistingAgent_DecreasesCount()
    {
        var tracker = new AgentConnectionTracker();
        tracker.Register("agent-1", CreateInfo());

        tracker.Unregister("agent-1");

        tracker.Count.Should().Be(0);
    }

    [Fact]
    public void Unregister_NonExistentAgent_DoesNotThrow()
    {
        var tracker = new AgentConnectionTracker();

        var act = () => tracker.Unregister("does-not-exist");

        act.Should().NotThrow();
    }

    [Fact]
    public void Unregister_AgentTwice_DoesNotThrow()
    {
        var tracker = new AgentConnectionTracker();
        tracker.Register("agent-1", CreateInfo());
        tracker.Unregister("agent-1");

        var act = () => tracker.Unregister("agent-1");

        act.Should().NotThrow();
        tracker.Count.Should().Be(0);
    }

    // ── Get ──

    [Fact]
    public void Get_ExistingAgent_ReturnsInfo()
    {
        var tracker = new AgentConnectionTracker();
        var info = CreateInfo();
        tracker.Register("agent-1", info);

        var result = tracker.Get("agent-1");

        result.Should().NotBeNull();
        result!.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public void Get_NonExistentAgent_ReturnsNull()
    {
        var tracker = new AgentConnectionTracker();

        tracker.Get("no-such-agent").Should().BeNull();
    }

    // ── GetAll ──

    [Fact]
    public void GetAll_Empty_ReturnsEmptyList()
    {
        var tracker = new AgentConnectionTracker();

        tracker.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void GetAll_WithAgents_ReturnsAllRegistered()
    {
        var tracker = new AgentConnectionTracker();
        tracker.Register("a1", CreateInfo("a1"));
        tracker.Register("a2", CreateInfo("a2"));

        var all = tracker.GetAll();

        all.Should().HaveCount(2);
        all.Select(a => a.AgentId).Should().Contain(["a1", "a2"]);
    }

    // ── Count ──

    [Fact]
    public void Count_Empty_ReturnsZero()
    {
        var tracker = new AgentConnectionTracker();

        tracker.Count.Should().Be(0);
    }

    [Fact]
    public void Count_AfterRegisterAndUnregister_ReflectsCurrentState()
    {
        var tracker = new AgentConnectionTracker();

        tracker.Register("a1", CreateInfo("a1"));
        tracker.Register("a2", CreateInfo("a2"));
        tracker.Count.Should().Be(2);

        tracker.Unregister("a1");
        tracker.Count.Should().Be(1);
    }

    // ── Concurrent access ──

    [Fact]
    public void ConcurrentRegistrations_DoNotLoseData()
    {
        var tracker = new AgentConnectionTracker();

        Parallel.For(0, 100, i =>
        {
            var id = $"agent-{i}";
            tracker.Register(id, CreateInfo(id));
        });

        tracker.Count.Should().Be(100);
    }
}
