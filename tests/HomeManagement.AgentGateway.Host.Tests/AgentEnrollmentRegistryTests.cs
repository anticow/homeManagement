using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using HomeManagement.Abstractions.Models;
using HomeManagement.Agent.Protocol;
using HomeManagement.AgentGateway.Host.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HomeManagement.AgentGateway.Host.Tests;

public sealed class AgentEnrollmentRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hm-enrollment-{Guid.NewGuid():N}");
    private readonly string _directory;
    private readonly string _keyPath;
    private readonly Guid _machineId = Guid.NewGuid();

    public AgentEnrollmentRegistryTests()
    {
        _directory = Path.Combine(_root, "state");
        _keyPath = Path.Combine(_root, "wrapping-key");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        EncryptedAgentEnrollmentStore.Initialize(_directory, _keyPath,
        [
            new("domain-a-node", "node.a.example", "NODE", _machineId),
            new("domain-b-node", "node.b.example", "node"),
            new("rejected-node", "rejected.a.example", "rejected", Revoked: true)
        ]);
    }

    private AgentEnrollmentRegistry Open(Action<EnrollmentCommitStage>? hook = null) =>
        new(new EncryptedAgentEnrollmentStore(_directory, _keyPath, hook));

    private static AgentEnrollmentEntry Ensure(AgentEnrollmentRegistry registry, string id = "domain-a-node") =>
        id == "domain-a-node"
            ? registry.Ensure(id, "node.a.example", "node")
            : registry.Ensure(id, "node.b.example", "node");

    private static StandaloneAgentGatewayService Gateway(AgentEnrollmentRegistry registry) =>
        new(NullLogger<StandaloneAgentGatewayService>.Instance, new NullRevokedAgentStore(), registry);

    private static AgentGatewaySession Connect(StandaloneAgentGatewayService gateway, string key,
        Action? abort = null, string id = "domain-a-node", string hostname = "node", bool encrypted = true) =>
        gateway.AuthenticateAndRegister(Substitute.For<IAgentApiKeyValidator>(),
            new TestServerCallContext(new Metadata { { "x-agent-api-key", key } }),
            new Handshake { AgentId = id, Hostname = hostname },
            Substitute.For<IServerStreamWriter<ControlMessage>>(), encrypted, abort ?? (() => { }));

    [Fact]
    public void PendingAdmission_IsExplicit_EnsureIsIdempotent_AndKeysAreDistinct()
    {
        using var registry = Open();
        var entry = Ensure(registry);
        Ensure(registry).Should().Be(entry);
        entry.Metadata.State.Should().Be(AgentEnrollmentState.Pending);
        registry.Authenticate("DOMAIN-A-NODE", "NODE.", entry.Credential, true).Should().Be(1);
        Ensure(registry).Metadata.State.Should().Be(AgentEnrollmentState.Pending);
        registry.ForMachine(_machineId).AgentId.Should().Be("domain-a-node");
        Ensure(registry, "domain-b-node").Credential.Should().NotBe(entry.Credential);
        entry.ToString().Should().NotContain(entry.Credential);
        JsonSerializer.Serialize(registry.List()).Should().NotContain(entry.Credential);
        File.ReadAllText(Path.Combine(_directory, "registry.json")).Should().NotContain(entry.Credential);
    }

    [Fact]
    public void Authentication_BindsIdKeyWireHostnameAndTls_NotInventoryFqdnOrAnotherNode()
    {
        using var registry = Open();
        var key = Ensure(registry).Credential;
        var otherKey = Ensure(registry, "domain-b-node").Credential;
        foreach (var (id, host, credential, tls) in new[]
        {
            ("unknown", "node", key, true), ("domain-a-node", "node", otherKey, true),
            ("domain-b-node", "node", key, true), ("domain-a-node", "node.a.example", key, true),
            ("domain-a-node", "other", key, true), ("domain-a-node", "node", "bad-key", true),
            ("domain-a-node", "node", key, false)
        })
        {
            var authenticate = () => registry.Authenticate(id, host, credential, tls);
            authenticate.Should().Throw<Exception>();
        }
        registry.Authenticate("domain-b-node", "node", otherKey, true).Should().Be(1);
        var mismatch = () => registry.Ensure("domain-a-node", "node.b.example", "node");
        mismatch.Should().Throw<InvalidOperationException>();
        var missing = () => registry.Ensure("unknown", "unknown.example", "unknown");
        missing.Should().Throw<KeyNotFoundException>();
        var revoked = () => registry.Ensure("rejected-node", "rejected.a.example", "rejected");
        revoked.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Lifecycle_CasVersionsFreshKeysTombstonesAndExplicitRebindingPersist()
    {
        string original;
        string rotated;
        using (var registry = Open())
        {
            original = Ensure(registry).Credential;
            registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Rotate).Version.Should().Be(2);
            rotated = Ensure(registry).Credential;
            rotated.Should().NotBe(original);
            var old = () => registry.Authenticate("domain-a-node", "node", original, true);
            old.Should().Throw<InvalidOperationException>();
            var stale = () => registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Revoke);
            stale.Should().Throw<InvalidOperationException>().WithMessage("*version conflict*");
            var reenableEnabled = () => registry.Transition("domain-a-node", 2, AgentEnrollmentTransition.Reenable);
            reenableEnabled.Should().Throw<InvalidOperationException>();
            var rebindEnabled = () => registry.Rebind("domain-a-node", 2, "other.example", "other", _machineId);
            rebindEnabled.Should().Throw<InvalidOperationException>();
            registry.Transition("domain-a-node", 2, AgentEnrollmentTransition.Revoke).Version.Should().Be(3);
            var revokedRotate = () => registry.Transition("domain-a-node", 3, AgentEnrollmentTransition.Rotate);
            revokedRotate.Should().Throw<InvalidOperationException>();
            registry.Rebind("domain-a-node", 3, "new.example", "new", _machineId).Version.Should().Be(4);
        }
        using var reopened = Open();
        var metadata = reopened.ForMachine(_machineId);
        metadata.State.Should().Be(AgentEnrollmentState.Revoked);
        metadata.CredentialGeneration.Should().Be(4);
        var ensureRevoked = () => reopened.Ensure("domain-a-node", "new.example", "new");
        ensureRevoked.Should().Throw<InvalidOperationException>();
        reopened.Transition("domain-a-node", 4, AgentEnrollmentTransition.Reenable).Version.Should().Be(5);
        var fresh = reopened.Ensure("domain-a-node", "new.example", "new");
        fresh.Credential.Should().NotBe(original).And.NotBe(rotated);
        reopened.Authenticate("domain-a-node", "new", fresh.Credential, true).Should().Be(5);
        reopened.List().Single(e => e.AgentId == "rejected-node").State.Should().Be(AgentEnrollmentState.Revoked);
    }

    [Fact]
    public void DuplicateIdInventoryMachineAndCredentialBindingsAreRejected()
    {
        using var registry = Open();
        foreach (var assignment in new AgentEnrollmentAssignment[]
        {
            new("DOMAIN-A-NODE", "other.example", "other"),
            new("new-id", "NODE.A.EXAMPLE.", "other"),
            new("new-id", "other.example", "other", _machineId),
            new("bad id", "other.example", "other"),
            new("new-id", "bad..example", "other"),
            new("new-id", "other.example", "other", Guid.Empty)
        })
        {
            var assign = () => registry.Assign(assignment);
            assign.Should().Throw<Exception>();
            registry.IsHealthy.Should().BeTrue();
        }
        var document = AgentEnrollmentRegistry.CreateDocument(
            [new("one", "one.example", "one"), new("two", "two.example", "two")]);
        var duplicate = document with
        {
            Entries = [document.Entries[0], document.Entries[1] with { Credential = document.Entries[0].Credential }]
        };
        var validate = () => AgentEnrollmentRegistry.Validate(duplicate);
        validate.Should().Throw<InvalidDataException>();
        registry.Assign(new("new-id", "other.example", "node")).Version.Should().Be(1);
    }

    [Theory]
    [InlineData("registry.json")]
    [InlineData("initialized.json")]
    public void MissingInitializedStateIsNotRecreated(string filename)
    {
        File.Delete(Path.Combine(_directory, filename));
        var open = () => Open();
        open.Should().Throw<FileNotFoundException>();
        File.Exists(Path.Combine(_directory, filename)).Should().BeFalse();
    }

    [Fact]
    public void WrongMarkerWrongWrappingKeyAndTruncatedCiphertextFailStartup()
    {
        var markerPath = Path.Combine(_directory, "initialized.json");
        var marker = File.ReadAllBytes(markerPath);
        File.WriteAllText(markerPath, JsonSerializer.Serialize(Guid.NewGuid()));
        ((Action)(() => Open())).Should().Throw<InvalidDataException>();
        File.WriteAllBytes(markerPath, marker);
        var key = File.ReadAllBytes(_keyPath);
        File.WriteAllText(_keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        ((Action)(() => Open())).Should().Throw<CryptographicException>();
        File.WriteAllBytes(_keyPath, key);
        File.WriteAllText(Path.Combine(_directory, "registry.json"), "{\"Format\":1");
        ((Action)(() => Open())).Should().Throw<JsonException>();
    }

    [Fact]
    public void AuthenticatedButMalformedLoadedStateIsRejected()
    {
        var document = AgentEnrollmentRegistry.CreateDocument(
            [new("one", "one.example", "one"), new("two", "two.example", "two")]);
        WriteFixture(document with { Entries = [document.Entries[0], document.Entries[0]] });
        ((Action)(() => Open())).Should().Throw<InvalidDataException>();
        WriteFixture(document with
        {
            Entries = [document.Entries[0], document.Entries[1] with { Credential = document.Entries[0].Credential }]
        });
        ((Action)(() => Open())).Should().Throw<InvalidDataException>();
        var binding = Guid.NewGuid();
        WriteFixture(document with
        {
            Entries = document.Entries.Select(e => e with { Metadata = e.Metadata with { MachineId = binding } }).ToArray()
        });
        ((Action)(() => Open())).Should().Throw<InvalidDataException>();
        WriteFixture(document with { Entries = [document.Entries[0] with { Metadata = document.Entries[0].Metadata with { Version = 0 } }] });
        ((Action)(() => Open())).Should().Throw<InvalidDataException>();
        File.WriteAllBytes(Path.Combine(_directory, "registry.json"), new byte[EncryptedAgentEnrollmentStore.MaximumStateBytes + 1]);
        ((Action)(() => Open())).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void EnvelopeMetadataAndTagAreAuthenticated()
    {
        var path = Path.Combine(_directory, "registry.json");
        var original = File.ReadAllText(path);
        foreach (var member in new[] { "Sequence", "Tag", "Nonce", "Ciphertext" })
        {
            var envelope = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
            if (member == "Sequence") envelope[member] = 9;
            else
            {
                var bytes = Convert.FromBase64String(envelope[member]!.GetValue<string>());
                bytes[0] ^= 1;
                envelope[member] = Convert.ToBase64String(bytes);
            }
            File.WriteAllText(path, envelope.ToJsonString());
            ((Action)(() => Open())).Should().Throw<CryptographicException>();
        }
        File.WriteAllText(path, original);
        using var valid = Open();
        valid.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void LifetimeLockRejectsOtherWriters_AndNormalStartupDoesNotInitialize()
    {
        using (var registry = Open())
        {
            ((Action)(() => Open())).Should().Throw<IOException>();
            ((Action)(() => EncryptedAgentEnrollmentStore.Initialize(_directory, _keyPath, [])))
                .Should().Throw<InvalidOperationException>();
        }
        using var reopened = Open();
        reopened.List().Should().HaveCount(3);
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        Action openEmpty = () =>
        {
            using var unexpected = new AgentEnrollmentRegistry(new EncryptedAgentEnrollmentStore(empty, _keyPath));
            unexpected.IsHealthy.Should().BeFalse();
        };
        openEmpty.Should().Throw<FileNotFoundException>();
        File.Exists(Path.Combine(empty, "registry.json")).Should().BeFalse();
    }

    [Theory]
    [InlineData((int)EnrollmentCommitStage.BeforeWrite, false)]
    [InlineData((int)EnrollmentCommitStage.BeforeReplace, false)]
    [InlineData((int)EnrollmentCommitStage.AfterReplace, true)]
    public async Task CommitFault_FencesLiveSessionsAndReadiness_NoPartialAcknowledgement(
        int stage, bool committed)
    {
        using (var registry = Open(current => { if ((int)current == stage) throw new IOException("Synthetic storage fault."); }))
        using (var gateway = Gateway(registry))
        {
            var aborted = 0;
            var session = Connect(gateway, Ensure(registry).Credential, () => Interlocked.Increment(ref aborted));
            var other = Connect(gateway, Ensure(registry, "domain-b-node").Credential,
                () => Interlocked.Increment(ref aborted), id: "domain-b-node");
            var pending = gateway.SendCommandAsync("domain-a-node", new RemoteCommand("", TimeSpan.FromSeconds(10)));
            ((Action)(() => registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Rotate)))
                .Should().Throw<IOException>();
            registry.IsHealthy.Should().BeFalse();
            (await registry.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Unhealthy);
            aborted.Should().Be(2);
            gateway.CanSend(session).Should().BeFalse();
            gateway.CanSend(other).Should().BeFalse();
            gateway.GetConnectedAgents().Should().BeEmpty();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            ((Action)(() => Ensure(registry))).Should().Throw<InvalidOperationException>();
            ((Action)(() => Connect(gateway, "bad"))).Should().Throw<RpcException>();
        }
        using var reopened = Open();
        Ensure(reopened).Metadata.Version.Should().Be(committed ? 2 : 1);
        Directory.EnumerateFiles(_directory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Rotate_FencesQueuedWorkOldReceiveAndCleanup_WithoutRebindingReplacement()
    {
        using var registry = Open();
        using var gateway = Gateway(registry);
        var oldKey = Ensure(registry).Credential;
        var aborted = false;
        var old = Connect(gateway, oldKey, () =>
        {
            Monitor.IsEntered(registry.SyncRoot).Should().BeFalse();
            aborted = true;
        });
        var pending = gateway.SendCommandAsync("domain-a-node", new RemoteCommand("", TimeSpan.FromSeconds(10)));
        var queued = await old.Outbound.Reader.ReadAsync();
        registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Rotate);
        aborted.Should().BeTrue();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        ((Action)(() => Connect(gateway, oldKey))).Should().Throw<RpcException>();
        var current = Connect(gateway, Ensure(registry).Credential);
        var lastHeartbeat = current.LastHeartbeatUtc;
        ((Action)(() => gateway.UpdateHeartbeat(old, new Heartbeat()))).Should().Throw<InvalidOperationException>();
        ((Action)(() => gateway.CompleteCommand(old, new CommandResponse { RequestId = queued.Message.CommandRequest.RequestId })))
            .Should().Throw<InvalidOperationException>();
        gateway.CanSend(old).Should().BeFalse();
        gateway.UnregisterAgent(old.AgentId, old);
        gateway.CanSend(current).Should().BeTrue();
        current.LastHeartbeatUtc.Should().Be(lastHeartbeat);
        gateway.UpdateHeartbeat(current, new Heartbeat());
        ((Action)(() => gateway.RevokeAgent("domain-a-node", "legacy endpoint"))).Should().Throw<InvalidOperationException>();
        registry.Transition("domain-a-node", 2, AgentEnrollmentTransition.Revoke);
        gateway.CanSend(current).Should().BeFalse();
        ((Action)(() => Connect(gateway, Ensure(registry, "domain-b-node").Credential))).Should().Throw<RpcException>();
    }

    [Fact]
    public async Task AdmissionAndRotationAreSerialized_UsingCommitBarrier()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registry = Open(stage =>
        {
            if (stage != EnrollmentCommitStage.BeforeReplace) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test barrier timed out.");
        });
        using var gateway = Gateway(registry);
        var key = Ensure(registry).Credential;
        var rotate = Task.Run(() => registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Rotate));
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        using var attempted = new ManualResetEventSlim();
        var admission = Task.Run(() =>
        {
            attempted.Set();
            return Record.Exception(() => Connect(gateway, key));
        });
        try { attempted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(); }
        finally { release.Set(); }
        await rotate;
        (await admission).Should().BeOfType<RpcException>();
        gateway.GetConnectedAgents().Should().BeEmpty();
    }

    [Fact]
    public async Task AdmissionBeforeRotationNotification_CannotKeepOldSessionOrLoseNewSession()
    {
        using var registry = Open();
        using var notification = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Hold notification outside the shared lock, simulating a delayed transport abort.
        Action holdNotification = () =>
        {
            Monitor.IsEntered(registry.SyncRoot).Should().BeFalse();
            notification.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test barrier timed out.");
        };
        registry.Changed += holdNotification;
        using var gateway = Gateway(registry);
        var aborted = 0;
        var old = Connect(gateway, Ensure(registry).Credential, () => Interlocked.Increment(ref aborted));
        var rotate = Task.Run(() => registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Rotate));
        notification.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        AgentGatewaySession current;
        try { current = Connect(gateway, Ensure(registry).Credential); }
        finally { release.Set(); }
        await rotate;
        gateway.CanSend(current).Should().BeTrue();
        gateway.CanSend(old).Should().BeFalse();
        aborted.Should().Be(1);
        registry.Changed -= holdNotification;
    }

    [Fact]
    public async Task ThrowingOldAbort_DoesNotOrphanGrpcAdmissionOrPreventReconnect()
    {
        using var registry = Open();
        using var notification = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Action holdNotification = () =>
        {
            notification.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test barrier timed out.");
        };
        registry.Changed += holdNotification;
        using var gateway = Gateway(registry);
        var old = Connect(gateway, Ensure(registry).Credential, () =>
        {
            Monitor.IsEntered(registry.SyncRoot).Should().BeFalse();
            throw new InvalidOperationException("Synthetic old transport abort failure.");
        });
        var pending = gateway.SendCommandAsync("domain-a-node", new RemoteCommand("", TimeSpan.FromSeconds(10)));
        var rotate = Task.Run(() => registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Rotate));
        notification.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        var context = new TestServerCallContext(new Metadata { { "x-agent-api-key", Ensure(registry).Credential } });
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        context.UserState["__HttpContext"] = http;
        var requests = new TestAsyncStreamReader<AgentMessage>(
            [new AgentMessage { Handshake = new Handshake { AgentId = "domain-a-node", Hostname = "node" } }]);
        var writer = new BlockingAckWriter();
        var service = new AgentGatewayGrpcService(Substitute.For<IAgentApiKeyValidator>(), gateway,
            NullLogger<AgentGatewayGrpcService>.Instance);
        Task connection;
        try
        {
            connection = service.Connect(requests, writer, context);
            await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            writer.Release.TrySetResult();
            registry.Changed -= holdNotification;
        }
        await rotate;
        await connection.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        old.PendingCommands.Should().BeEmpty();
        gateway.CanSend(old).Should().BeFalse();
        gateway.GetConnectedAgents().Should().BeEmpty();
        var reconnect = Connect(gateway, Ensure(registry).Credential);
        gateway.CanSend(reconnect).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingNotification_AttemptsEverySubscriberAndRetainsPersistenceFailure(bool storageFault)
    {
        var storageError = new IOException("Synthetic storage failure.");
        using var registry = Open(stage =>
        {
            if (storageFault && stage == EnrollmentCommitStage.BeforeReplace) throw storageError;
        });
        Action throwingSubscriber = () => throw new InvalidOperationException("Synthetic notification failure.");
        registry.Changed += throwingSubscriber;
        using var gateway = Gateway(registry);
        var aborted = 0;
        var session = Connect(gateway, Ensure(registry).Credential, () => aborted++);
        var pending = gateway.SendCommandAsync("domain-a-node", new RemoteCommand("", TimeSpan.FromSeconds(10)));
        var laterSubscriberCalled = false;
        Action laterSubscriber = () => laterSubscriberCalled = true;
        registry.Changed += laterSubscriber;
        Exception? failure;
        try
        {
            failure = Record.Exception(() => registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Revoke));
        }
        finally
        {
            registry.Changed -= throwingSubscriber;
            registry.Changed -= laterSubscriber;
        }
        if (storageFault)
        {
            failure.Should().BeOfType<AggregateException>().Which.InnerExceptions[0].Should().BeSameAs(storageError);
            ((AggregateException)failure!).InnerExceptions.Should().HaveCount(2);
            registry.IsHealthy.Should().BeFalse();
        }
        else failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("notification");
        laterSubscriberCalled.Should().BeTrue();
        aborted.Should().Be(1);
        gateway.CanSend(session).Should().BeFalse();
        gateway.GetConnectedAgents().Should().BeEmpty();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task RevocationSerializesWithAdmission_AndSurvivesRestart()
    {
        string key;
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var registry = Open(stage =>
        {
            if (stage != EnrollmentCommitStage.BeforeReplace) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test barrier timed out.");
        }))
        using (var gateway = Gateway(registry))
        {
            key = Ensure(registry).Credential;
            var revoke = Task.Run(() => registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Revoke));
            entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var admission = Task.Run(() => Record.Exception(() => Connect(gateway, key)));
            release.Set();
            await revoke;
            (await admission).Should().BeOfType<RpcException>();
            gateway.GetConnectedAgents().Should().BeEmpty();
        }
        using var restored = Open();
        using var restoredGateway = Gateway(restored);
        ((Action)(() => Connect(restoredGateway, key))).Should().Throw<RpcException>();
        ((Action)(() => Ensure(restored))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MissingStateDuringMutationFencesStorageInsteadOfReplacingLostState()
    {
        using var registry = Open();
        File.Delete(Path.Combine(_directory, "registry.json"));
        ((Action)(() => registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Rotate)))
            .Should().Throw<FileNotFoundException>();
        registry.IsHealthy.Should().BeFalse();
        File.Exists(Path.Combine(_directory, "registry.json")).Should().BeFalse();
    }

    [Fact]
    public void ManagedModeDoesNotConsultLegacyRevocationsOrAllowUnauthenticatedRegistration()
    {
        using var registry = Open();
        var legacy = Substitute.For<IRevokedAgentStore>();
        legacy.IsRevoked(Arg.Any<string>()).Returns(true);
        using var gateway = new StandaloneAgentGatewayService(
            NullLogger<StandaloneAgentGatewayService>.Instance, legacy, registry);
        var session = Connect(gateway, Ensure(registry).Credential);
        gateway.CanSend(session).Should().BeTrue();
        legacy.DidNotReceive().IsRevoked(Arg.Any<string>());
        ((Action)(() => gateway.RegisterAgent(new Handshake { AgentId = "other" },
            Substitute.For<IServerStreamWriter<ControlMessage>>()))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task GrpcSendLoopRejectsBufferedCommandAfterRotation()
    {
        using var registry = Open();
        using var gateway = Gateway(registry);
        var context = new TestServerCallContext(new Metadata { { "x-agent-api-key", Ensure(registry).Credential } });
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        context.UserState["__HttpContext"] = http;
        var requests = new TestAsyncStreamReader<AgentMessage>(
            [new AgentMessage { Handshake = new Handshake { AgentId = "domain-a-node", Hostname = "node" } }]);
        var writer = new BlockingAckWriter();
        var service = new AgentGatewayGrpcService(Substitute.For<IAgentApiKeyValidator>(), gateway,
            NullLogger<AgentGatewayGrpcService>.Instance);
        var connection = service.Connect(requests, writer, context);
        await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = gateway.SendCommandAsync("domain-a-node", new RemoteCommand("", TimeSpan.FromSeconds(10)));
        try { registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Rotate); }
        finally { writer.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<RpcException>(() => connection.WaitAsync(TimeSpan.FromSeconds(10)));
        writer.Messages.Should().ContainSingle().Which.PayloadCase.Should().Be(ControlMessage.PayloadOneofCase.Ack);
    }

    [Theory]
    [InlineData("Shell")]
    [InlineData("ManagedServices")]
    public async Task MachineCommands_SelectExplicitGuidDespiteEqualWireHostnames(string type)
    {
        using var registry = Open();
        using var gateway = Gateway(registry);
        var first = Connect(gateway, Ensure(registry).Credential);
        var secondId = Guid.NewGuid();
        registry.Assign(new("explicit-second", "node.c.example", "node", secondId));
        var second = Connect(gateway, registry.Ensure("explicit-second", "node.c.example", "node").Credential, id: "explicit-second");
        var command = gateway.SendMachineCommandAsync(secondId, new("", TimeSpan.FromSeconds(10), CommandType: type));
        var outbound = await second.Outbound.Reader.ReadAsync();
        first.Outbound.Reader.TryRead(out _).Should().BeFalse();
        outbound.Binding!.MachineId.Should().Be(secondId);
        var writer = new RecordingWriter(registry.SyncRoot);
        await gateway.WriteOutboundAsync(second, outbound, writer, CancellationToken.None);
        gateway.CompleteCommand(second, new() { RequestId = outbound.Message.CommandRequest.RequestId, Stdout = "selected-second" });
        (await command).Stdout.Should().Be("selected-second");
        gateway.GetMachineMetadata(secondId).AgentId.Should().Be("explicit-second");
        gateway.GetMachineObservation(secondId).SessionId.Should().Be(second.SessionId);
        writer.Messages.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Shell", false)]
    [InlineData("ManagedServices", false)]
    [InlineData("Shell", true)]
    public async Task MachineFinalSender_RejectsCapturedWorkAfterRotationRevocationAndRebind(string type, bool rebind)
    {
        using var registry = Open();
        using var gateway = Gateway(registry);
        var old = Connect(gateway, Ensure(registry).Credential);
        var pending = gateway.SendMachineCommandAsync(_machineId, new("", TimeSpan.FromSeconds(10), CommandType: type));
        var outbound = await old.Outbound.Reader.ReadAsync();
        registry.Transition("domain-a-node", 1, rebind ? AgentEnrollmentTransition.Revoke : AgentEnrollmentTransition.Rotate);
        if (rebind)
        {
            registry.Rebind("domain-a-node", 2, "replacement.example", "replacement", Guid.NewGuid());
            registry.Transition("domain-a-node", 3, AgentEnrollmentTransition.Reenable);
        }
        var key = rebind ? registry.Ensure("domain-a-node", "replacement.example", "replacement").Credential : Ensure(registry).Credential;
        var replacement = Connect(gateway, key, hostname: rebind ? "replacement" : "node");
        var writer = new RecordingWriter(registry.SyncRoot);
        await Assert.ThrowsAsync<RpcException>(() => gateway.WriteOutboundAsync(old, outbound, writer, CancellationToken.None));
        writer.Messages.Should().BeEmpty();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        gateway.UnregisterAgent(old.AgentId, old);
        gateway.CanSend(replacement).Should().BeTrue();
        ((Action)(() => gateway.CompleteCommand(old, new() { RequestId = outbound.Message.CommandRequest.RequestId })))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task SameGenerationReplacement_CannotReceiveOldCapturedUpdateOrCompletion()
    {
        using var registry = Open();
        using var gateway = Gateway(registry);
        var key = Ensure(registry).Credential;
        var old = Connect(gateway, key);
        await gateway.RequestMachineUpdateAsync(_machineId, new("2", [], "https://example.test/agent"));
        var outbound = await old.Outbound.Reader.ReadAsync();
        gateway.UnregisterAgent(old.AgentId, old);
        var replacement = Connect(gateway, key);
        replacement.CredentialGeneration.Should().Be(old.CredentialGeneration);
        replacement.SessionId.Should().NotBe(old.SessionId);
        var writer = new RecordingWriter(registry.SyncRoot);
        await Assert.ThrowsAsync<RpcException>(() => gateway.WriteOutboundAsync(old, outbound, writer, CancellationToken.None));
        writer.Messages.Should().BeEmpty();
        gateway.UnregisterAgent(old.AgentId, old);
        gateway.GetMachineObservation(_machineId).SessionId.Should().Be(replacement.SessionId);
        await gateway.RequestMachineUpdateAsync(_machineId, new("3", [], "https://example.test/agent"));
        await gateway.WriteOutboundAsync(replacement, await replacement.Outbound.Reader.ReadAsync(), writer, CancellationToken.None);
        writer.Messages.Should().ContainSingle().Which.UpdateDirective.TargetVersion.Should().Be("3");
    }

    [Fact]
    public async Task QueueBackpressure_RevalidatesBeforeAdmissionAndNeverRetargets()
    {
        using var registry = Open();
        using var gateway = Gateway(registry);
        var old = Connect(gateway, Ensure(registry).Credential);
        for (var i = 0; i < 64; i++)
            await gateway.RequestMachineUpdateAsync(_machineId, new("2", [], "https://example.test/agent"));
        var blocked = gateway.RequestMachineUpdateAsync(_machineId, new("blocked", [], "https://example.test/agent"));
        blocked.IsCompleted.Should().BeFalse();
        registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Rotate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => blocked);
        var replacement = Connect(gateway, Ensure(registry).Credential);
        replacement.Outbound.Reader.TryRead(out _).Should().BeFalse();
        var writer = new RecordingWriter(registry.SyncRoot);
        await Assert.ThrowsAsync<RpcException>(async () => await gateway.WriteOutboundAsync(old, await old.Outbound.Reader.ReadAsync(), writer, CancellationToken.None));
        writer.Messages.Should().BeEmpty();
    }

    [Fact]
    public void MachineObservations_RejectUnknownAndRevokedButRepresentBoundDisconnectedSeparately()
    {
        using var registry = Open();
        using var gateway = Gateway(registry);
        gateway.GetMachineObservation(_machineId).Connected.Should().BeFalse();
        ((Action)(() => gateway.GetMachineMetadata(_machineId))).Should().Throw<InvalidOperationException>();
        ((Action)(() => gateway.GetMachineObservation(Guid.NewGuid()))).Should().Throw<KeyNotFoundException>();
        var session = Connect(gateway, Ensure(registry).Credential);
        var observation = gateway.GetMachineObservation(_machineId);
        observation.RegistryId.Should().Be(registry.RegistryId);
        observation.SessionId.Should().Be(session.SessionId);
        registry.Transition("domain-a-node", 1, AgentEnrollmentTransition.Revoke);
        ((Action)(() => gateway.GetMachineObservation(_machineId))).Should().Throw<InvalidOperationException>();
    }

    private sealed class RecordingWriter(object gate) : IServerStreamWriter<ControlMessage>
    {
        public WriteOptions? WriteOptions { get; set; }
        public List<ControlMessage> Messages { get; } = [];
        public Task WriteAsync(ControlMessage message)
        {
            Monitor.IsEntered(gate).Should().BeFalse();
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingAckWriter : IServerStreamWriter<ControlMessage>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ControlMessage> Messages { get; } = [];
        public WriteOptions? WriteOptions { get; set; }

        public async Task WriteAsync(ControlMessage message)
        {
            Messages.Add(message);
            if (message.PayloadCase == ControlMessage.PayloadOneofCase.Ack)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private void WriteFixture(AgentEnrollmentDocument document)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var plain = JsonSerializer.SerializeToUtf8Bytes(document);
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(Convert.FromBase64String(File.ReadAllText(_keyPath)), 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"hm-agent-enrollment:1:{document.RegistryId:D}:{document.Sequence}")));
        File.WriteAllText(Path.Combine(_directory, "registry.json"), JsonSerializer.Serialize(new
        {
            Format = 1,
            document.RegistryId,
            document.Sequence,
            Nonce = nonce,
            Tag = tag,
            Ciphertext = cipher
        }));
        File.WriteAllText(Path.Combine(_directory, "initialized.json"), JsonSerializer.Serialize(document.RegistryId));
    }

    public void Dispose()
    {
        // Only this fixture's freshly allocated, explicit temporary directory is removed.
        Directory.Delete(_root, recursive: true);
    }
}
