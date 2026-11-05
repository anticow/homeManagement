using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Broker.Host.Endpoints;
using HomeManagement.Broker.Host.Services;
using HomeManagement.Core;
using HomeManagement.Data;
using HomeManagement.Data.SqlServer;
using HomeManagement.Inventory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomeManagement.Web.Tests;

public sealed partial class DeviceStateProjectionTests
{
    [Fact]
    public async Task FailuresRetainSuccessAndHeartbeatAcrossScopesAndBecomeUnknownAtExpiry()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Validate();
        var first = await fixture.Read();
        first.Connectivity.Should().Be(DeviceConnectivity.Online);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(300);
        fixture.Runtime.GetMachineObservationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<MachineAgentObservation>(new HttpRequestException("secret upstream detail")));
        fixture.Health.GetAgentHealthAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AgentHealthSummary>(new HttpRequestException("secret metric detail")));
        await fixture.Validate();
        var retained = await fixture.Read();
        retained.Snapshot.AgentSuccessUtc.Should().Be(first.Snapshot.AgentSuccessUtc);
        retained.Snapshot.AgentAttemptUtc.Should().Be(fixture.Clock.Now);
        retained.Snapshot.LastHeartbeatUtc.Should().Be(first.Snapshot.LastHeartbeatUtc);
        retained.Snapshot.LastKnownConnected.Should().BeTrue();
        retained.Snapshot.Health.Should().BeEquivalentTo(first.Snapshot.Health);
        retained.Snapshot.MetricsSuccessUtc.Should().Be(first.Snapshot.MetricsSuccessUtc);
        retained.Snapshot.MetricsAttemptUtc.Should().Be(fixture.Clock.Now);
        retained.Snapshot.AgentFailure.Should().Be("Gateway validation unavailable.");
        retained.Snapshot.MetricsFailure.Should().Be("Prometheus/exporter metrics unavailable.");
        retained.Connectivity.Should().Be(DeviceConnectivity.Online);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1199);
        (await fixture.Read()).AgentIsStale.Should().BeFalse();
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        var stale = await fixture.Read();
        stale.Connectivity.Should().Be(DeviceConnectivity.Unknown);
        stale.AgentIsStale.Should().BeTrue();
        stale.MetricsIsStale.Should().BeTrue();
        stale.Snapshot.LastKnownConnected.Should().BeTrue();
        await fixture.Fresh.DidNotReceiveWithAnyArgs().ReadFreshLegacySnapshotAsync(default);
    }

    [Fact]
    public async Task AuthoritativeDisconnectRetainsLastSeenAndDoesNotMutateHistoricalInventory()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Validate();
        var connected = await fixture.Read();
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(300);
        fixture.Runtime.GetMachineObservationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(fixture.Observation(call.ArgAt<Guid>(0)) with
            { Connected = false, SessionId = null, LastHeartbeatUtc = null }));
        await fixture.Validate();
        var disconnected = await fixture.Read();
        disconnected.Connectivity.Should().Be(DeviceConnectivity.Offline);
        disconnected.Snapshot.LastHeartbeatUtc.Should().Be(connected.Snapshot.LastHeartbeatUtc);
        disconnected.Snapshot.AgentSuccessUtc.Should().Be(fixture.Clock.Now);
        disconnected.Machine.State.Should().Be(MachineState.Maintenance);
    }

    [Fact]
    public async Task MetricsAndPerMachineAgentFailuresAreIndependentAndBindingMismatchIsRejected()
    {
        await using var fixture = await Fixture.Create(3);
        fixture.Runtime.GetMachineObservationAsync(fixture.Machines[0].Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(fixture.Observation(Guid.NewGuid())));
        fixture.Health.GetAgentHealthAsync(fixture.Machines[1].Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AgentHealthSummary>(new HttpRequestException()));
        await fixture.Validate();
        var rows = await fixture.ReadAll();
        rows[0].Connectivity.Should().Be(DeviceConnectivity.Unknown);
        rows[0].Snapshot.AgentSuccessUtc.Should().BeNull();
        rows[0].MetricsIsStale.Should().BeFalse();
        rows[1].Connectivity.Should().Be(DeviceConnectivity.Online);
        rows[1].MetricsIsStale.Should().BeTrue();
        rows[2].Connectivity.Should().Be(DeviceConnectivity.Online);
    }

    [Fact]
    public async Task NeverValidatedNonAgentDeletedAndAllInventoryPagesAreHandled()
    {
        await using var fixture = await Fixture.Create(103);
        using (var scope = fixture.Services.CreateScope())
        {
            var machines = scope.ServiceProvider.GetRequiredService<IMachineRepository>();
            await machines.UpdateAsync(fixture.Machines[0] with { Protocol = TransportProtocol.Ssh });
            await machines.SoftDeleteAsync(fixture.Machines[1].Id);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        var initial = await fixture.ReadAll();
        initial.Single(d => d.Machine.Id == fixture.Machines[0].Id).Connectivity.Should().Be(DeviceConnectivity.NotApplicable);
        initial.Single(d => d.Machine.Id == fixture.Machines[2].Id).Connectivity.Should().Be(DeviceConnectivity.Unknown);
        initial.Should().NotContain(d => d.Machine.Id == fixture.Machines[1].Id);
        await fixture.Validate();
        var rows = await fixture.ReadAll();
        rows.Should().HaveCount(102);
        rows.Where(d => d.Connectivity != DeviceConnectivity.NotApplicable)
            .Should().OnlyContain(d => d.Connectivity == DeviceConnectivity.Online);
        await fixture.Runtime.DidNotReceive().GetMachineObservationAsync(fixture.Machines[0].Id, Arg.Any<CancellationToken>());
        await fixture.Runtime.DidNotReceive().GetMachineObservationAsync(fixture.Machines[1].Id, Arg.Any<CancellationToken>());
        await fixture.Runtime.Received(1).GetMachineObservationAsync(fixture.Machines[102].Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellationIsThreadedAndPersistedAttemptDoesNotInventOffline()
    {
        await using var fixture = await Fixture.Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        fixture.Runtime.GetMachineObservationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            observedToken = call.ArgAt<CancellationToken>(1);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, observedToken);
            return fixture.Observation(call.ArgAt<Guid>(0));
        });
        using var cts = new CancellationTokenSource();
        var validation = fixture.Validate(cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await ((Func<Task>)(() => validation)).Should().ThrowAsync<OperationCanceledException>();
        observedToken.IsCancellationRequested.Should().BeTrue();
        var stored = await fixture.Read();
        stored.Snapshot.AgentAttemptUtc.Should().Be(fixture.Clock.Now);
        stored.Snapshot.AgentSuccessUtc.Should().BeNull();
        stored.Snapshot.LastKnownConnected.Should().BeNull();
        stored.Connectivity.Should().Be(DeviceConnectivity.Unknown);
    }

    [Fact]
    public async Task LegacyUsesFreshExactResolutionAndFailedReadNeverCertifiesCachedAbsence()
    {
        await using var fixture = await Fixture.Create(3);
        fixture.Runtime.VerifyEnrollmentModeAsync(Arg.Any<CancellationToken>()).Returns(false);
        var heartbeat = fixture.Clock.Now.AddMinutes(-1).UtcDateTime;
        fixture.Fresh.ReadFreshLegacySnapshotAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConnectedAgent>>(
        [
            new(fixture.Machines[0].Hostname.Value.ToUpperInvariant(), "unrelated", "1", heartbeat, heartbeat.AddSeconds(-1), TimeSpan.Zero, heartbeat.AddSeconds(-1)),
            new("hostname-first", fixture.Machines[0].Hostname.Value.ToUpperInvariant(), "1", heartbeat, heartbeat, TimeSpan.Zero, heartbeat),
            new(fixture.Machines[1].Hostname.Value.ToUpperInvariant(), "other", "1", heartbeat, heartbeat, TimeSpan.Zero, heartbeat),
            new("shortname-is-not-a-match", fixture.Machines[2].Hostname.Value + ".example", "1", heartbeat, heartbeat, TimeSpan.Zero)
        ]));
        await fixture.Validate();
        var rows = await fixture.ReadAll();
        rows[0].Snapshot.LastHeartbeatUtc.Should().Be(new DateTimeOffset(heartbeat));
        rows[1].Connectivity.Should().Be(DeviceConnectivity.Online);
        rows[2].Connectivity.Should().Be(DeviceConnectivity.Offline); // Only a successful fresh absence is authoritative.
        await fixture.Runtime.DidNotReceiveWithAnyArgs().GetMachineObservationAsync(default, default);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(300);
        fixture.Fresh.ReadFreshLegacySnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ConnectedAgent>>(new HttpRequestException("snapshot unavailable")));
        await fixture.Validate();
        var after = await fixture.ReadAll();
        after[0].Snapshot.AgentSuccessUtc.Should().Be(rows[0].Snapshot.AgentSuccessUtc);
        after[0].Snapshot.LastKnownConnected.Should().BeTrue();
        after[0].Snapshot.AgentFailure.Should().NotBeNull();
        await fixture.Fresh.Received(2).ReadFreshLegacySnapshotAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OldLegacySnapshotCanValidateConnectionButCannotInventActualHeartbeat()
    {
        await using var fixture = await Fixture.Create();
        fixture.Runtime.VerifyEnrollmentModeAsync(Arg.Any<CancellationToken>()).Returns(false);
        fixture.Fresh.ReadFreshLegacySnapshotAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ConnectedAgent>>(
        [
            new("legacy-agent", fixture.Machines[0].Hostname.Value, "1",
                fixture.Clock.Now.UtcDateTime, fixture.Clock.Now.UtcDateTime, TimeSpan.Zero)
        ]));
        await fixture.Validate();
        var stored = await fixture.Read();
        stored.Connectivity.Should().Be(DeviceConnectivity.Online);
        stored.Snapshot.LastHeartbeatUtc.Should().BeNull();
    }

    [Fact]
    public async Task ModeVerificationFailureDoesNotFallBackAndMetricsStillValidate()
    {
        await using var fixture = await Fixture.Create();
        fixture.Runtime.VerifyEnrollmentModeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("mode mismatch")));
        await fixture.Validate();
        var row = await fixture.Read();
        row.Connectivity.Should().Be(DeviceConnectivity.Unknown);
        row.MetricsIsStale.Should().BeFalse();
        await fixture.Fresh.DidNotReceiveWithAnyArgs().ReadFreshLegacySnapshotAsync(default);
        await fixture.Runtime.DidNotReceiveWithAnyArgs().GetMachineObservationAsync(default, default);
    }

    [Fact]
    public async Task WorkerValidatesAtStartupAndEveryThreeHundredSecondsWithoutOverlappingCycles()
    {
        await using var fixture = await Fixture.Create();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maximumActive = 0;
        fixture.Runtime.GetMachineObservationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var concurrent = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, concurrent);
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(call.ArgAt<CancellationToken>(1));
            }
            else secondStarted.TrySetResult();
            Interlocked.Decrement(ref active);
            return fixture.Observation(call.ArgAt<Guid>(0));
        });
        using var worker = new DeviceStateValidationWorker(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeviceStateOptions()), fixture.Clock, NullLogger<DeviceStateValidationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var timer = fixture.Clock.Timer ?? throw new InvalidOperationException("Worker timer not created.");
            timer.Period.Should().Be(TimeSpan.FromSeconds(300));
            fixture.Clock.Now = fixture.Clock.Now.AddSeconds(300);
            timer.Tick();
            Volatile.Read(ref calls).Should().Be(1);
            releaseFirst.TrySetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            maximumActive.Should().Be(1);
        }
        finally
        {
            releaseFirst.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(timeout.Token);
        }
    }

    [Theory]
    [InlineData("", HttpStatusCode.Unauthorized)]
    [InlineData("NoPermission", HttpStatusCode.Forbidden)]
    [InlineData("Viewer", HttpStatusCode.OK)]
    public async Task FleetEndpointIsAuthorizedNoStoreAndReadsOnlyDatabase(string role, HttpStatusCode expected)
    {
        await using var fixture = await Fixture.Create();
        await fixture.Validate();
        fixture.Runtime.ClearReceivedCalls();
        fixture.Health.ClearReceivedCalls();
        fixture.Fresh.ClearReceivedCalls();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        fixture.Register(builder.Services);
        // A fleet read must work even when no network-capable evidence services are registered.
        builder.Services.RemoveAll<IMachineAgentRuntime>();
        builder.Services.RemoveAll<IFreshAgentSnapshotReader>();
        builder.Services.RemoveAll<IAgentHealthService>();
        builder.Services.RemoveAll<IRemoteExecutor>();
        builder.Services.RemoveAll<IInventoryService>();
        builder.Services.RemoveAll<DeviceStateCollector>();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDeviceStateEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        if (role.Length > 0) client.DefaultRequestHeaders.Add("X-Test-Role", role);
        using var response = await client.GetAsync("/api/device-state");
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
        {
            response.Headers.CacheControl?.NoStore.Should().BeTrue();
            var data = await response.Content.ReadFromJsonAsync<PagedResult<FleetDeviceState>>();
            data.Should().NotBeNull();
            data?.Items.Single().Connectivity.Should().Be(DeviceConnectivity.Online);
            using var invalid = await client.GetAsync("/api/device-state?pageSize=101");
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        fixture.Runtime.ReceivedCalls().Should().BeEmpty();
        fixture.Health.ReceivedCalls().Should().BeEmpty();
        fixture.Fresh.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void SqlServerMigrationGeneratesProjectionSchemaWithoutConnectingToServer()
    {
        var options = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlServer("Server=localhost;Database=projection-script-only;Integrated Security=true",
                sql => sql.MigrationsAssembly(typeof(SqlServerServiceExtensions).Assembly.FullName)).Options;
        using var db = new HomeManagementDbContext(options);
        var script = db.GetService<IMigrator>().GenerateScript("20260510030600_AddAgentVersionToMachine");
        script.Should().Contain("CREATE TABLE [DeviceStates]").And.Contain("[AgentSuccessUtc] datetime2")
            .And.Contain("[LastHeartbeatUtc] datetime2").And.Contain("[MetricsSuccessUtc] datetime2");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"hm-projection-{Guid.NewGuid():N}");
        private Action<IServiceCollection>? _configure;
        public ServiceProvider Services { get; private set; } = null!; // Initialized by Create before the fixture escapes.
        public Clock Clock { get; } = new();
        public IMachineAgentRuntime Runtime { get; } = Substitute.For<IMachineAgentRuntime>();
        public IFreshAgentSnapshotReader Fresh { get; } = Substitute.For<IFreshAgentSnapshotReader>();
        public IAgentHealthService Health { get; } = Substitute.For<IAgentHealthService>();
        public Machine[] Machines { get; private set; } = [];

        public static async Task<Fixture> Create(int count = 1, Action<IServiceCollection>? configure = null)
        {
            var fixture = new Fixture { _configure = configure };
            Directory.CreateDirectory(fixture._directory);
            var services = new ServiceCollection();
            fixture.Register(services);
            fixture._configure?.Invoke(services);
            fixture.Services = services.BuildServiceProvider();
            fixture.Runtime.VerifyEnrollmentModeAsync(Arg.Any<CancellationToken>()).Returns(true);
            fixture.Runtime.GetMachineObservationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(fixture.Observation(call.ArgAt<Guid>(0))));
            fixture.Health.GetAgentHealthAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(new AgentHealthSummary
                {
                    Id = call.ArgAt<Guid>(0),
                    IsOnline = true,
                    CpuUsagePercent = 12.5,
                    LastUpdatedUtc = fixture.Clock.Now.UtcDateTime
                }));
            fixture.Machines = Enumerable.Range(0, count).Select(i => new Machine(
                Guid.NewGuid(), Hostname.Create($"node-{i:D4}"), null, [], OsType.Linux, "Linux",
                MachineConnectionMode.Agent, TransportProtocol.Agent, 0, Guid.Empty, MachineState.Maintenance,
                new Dictionary<string, string>(), null, default, default, default)).ToArray();
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HomeManagementDbContext>();
            await db.Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<IMachineRepository>().AddRangeAsync(fixture.Machines);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            return fixture;
        }

        public void Register(IServiceCollection services)
        {
            services.AddLogging();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(Runtime);
            services.AddSingleton(Fresh);
            services.AddSingleton(Health);
            services.AddSingleton(Substitute.For<IRemoteExecutor>());
            services.AddHomeManagement(_directory, [typeof(InventoryModuleRegistration).Assembly]);
            services.AddScoped<DeviceStateCollector>();
        }

        public async Task Restart()
        {
            await Services.DisposeAsync();
            var services = new ServiceCollection();
            Register(services);
            _configure?.Invoke(services);
            Services = services.BuildServiceProvider();
        }

        public MachineAgentObservation Observation(Guid machineId) =>
            new(Guid.NewGuid(), machineId, "bound-agent", 7, 3, Guid.NewGuid(), true, Clock.Now, Clock.Now.AddMinutes(-1));

        public async Task Validate(CancellationToken ct = default)
        {
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DeviceStateCollector>().ValidateAsync(ct);
        }

        public async Task<FleetDeviceState> Read() => (await ReadAll()).Single();
        public async Task<IReadOnlyList<FleetDeviceState>> ReadAll()
        {
            await using var scope = Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IDeviceStateReader>().QueryAsync(new MachineQuery(PageSize: 1000))).Items;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 16, 0, 0, TimeSpan.Zero);
        public ManualTimer? Timer { get; private set; }
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new ManualTimer(callback, state, period);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        private bool _disposed;
        public TimeSpan Period { get; } = period;
        public void Tick() { if (!_disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            return Task.FromResult(role.Length == 0 ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "reader"), new Claim(ClaimTypes.Role, role)], Scheme.Name)), Scheme.Name)));
        }
    }
}
