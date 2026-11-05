using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Integration.Prometheus;
using HomeManagement.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomeManagement.Web.Tests;

public sealed partial class DeviceStateProjectionTests
{
    [Fact]
    public async Task BudgetLimitedCyclesPersistFairProgressPastSixtyFourSlowMachinesAndAcrossPages()
    {
        await using var fixture = await Fixture.Create(105);
        var slow = fixture.Machines.OrderBy(m => m.Id).Take(64).Select(m => m.Id).ToHashSet();
        var calls = new ConcurrentDictionary<Guid, int>();
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = 0;
        fixture.Runtime.GetMachineObservationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var id = call.ArgAt<Guid>(0);
            calls.AddOrUpdate(id, 1, (_, count) => count + 1);
            if (slow.Contains(id))
            {
                if (Interlocked.Increment(ref slowStarted) == 8) saturated.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
            }
            return fixture.Observation(id);
        });

        for (var cycle = 0; cycle < 9; cycle++)
        {
            slowStarted = 0;
            saturated = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Cancel deterministically once all eight slots are occupied by slow requests,
            // equivalent to budget expiry, without waiting 240 wall-clock seconds.
            await CancelSaturatedCycle(fixture, saturated.Task);
            fixture.Clock.Now = fixture.Clock.Now.AddSeconds(300);
            if (cycle == 3) await fixture.Restart();
            if (cycle == 7)
            {
                calls.Should().HaveCount(64);
                calls.Values.Should().OnlyContain(count => count == 1);
            }
        }

        var rows = await fixture.ReadAll();
        rows.Where(d => !slow.Contains(d.Machine.Id)).Should().HaveCount(41)
            .And.OnlyContain(d => d.Snapshot.AgentSuccessUtc.HasValue);
        // Every invocation uses a new collector/DB scope, including a provider restart halfway through.
        calls.Keys.Should().HaveCount(105);
    }

    [Fact]
    public async Task FairProgressSurvivesCancellationRenamesAdditionsAndSoftDeletionBetweenCycles()
    {
        await using var fixture = await Fixture.Create(103);
        var ordered = fixture.Machines.OrderBy(m => m.Id).ToArray();
        var slow = ordered.Take(8).Select(m => m.Id).ToHashSet();
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = 0;
        fixture.Runtime.GetMachineObservationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var id = call.ArgAt<Guid>(0);
            if (slow.Contains(id))
            {
                if (Interlocked.Increment(ref slowStarted) == 8) saturated.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
            }
            return fixture.Observation(id);
        });
        await CancelSaturatedCycle(fixture, saturated.Task);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(300);
        var added = ordered[9] with { Id = Guid.NewGuid(), Hostname = Hostname.Create("000-new") };
        using (var scope = fixture.Services.CreateScope())
        {
            var machines = scope.ServiceProvider.GetRequiredService<IMachineRepository>();
            await machines.SoftDeleteAsync(ordered[8].Id);
            await machines.UpdateAsync(ordered[0] with { Hostname = Hostname.Create("zzz-attempted") });
            await machines.UpdateAsync(ordered[102] with { Hostname = Hostname.Create("000-unvisited") });
            await machines.AddAsync(added);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await ((Func<Task>)(() => fixture.Validate(canceled.Token))).Should().ThrowAsync<OperationCanceledException>();
        }
        saturated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        slowStarted = 0;
        await CancelSaturatedCycle(fixture, saturated.Task);
        var rows = await fixture.ReadAll();
        rows.Should().HaveCount(103).And.NotContain(d => d.Machine.Id == ordered[8].Id);
        rows.Where(d => !slow.Contains(d.Machine.Id)).Should().OnlyContain(d => d.Snapshot.AgentSuccessUtc.HasValue);
        rows.Single(d => d.Machine.Id == added.Id).Snapshot.AgentSuccessUtc.Should().NotBeNull();
        rows.Single(d => d.Machine.Id == ordered[102].Id).Snapshot.AgentSuccessUtc.Should().NotBeNull();
        await fixture.Runtime.DidNotReceive().GetMachineObservationAsync(ordered[8].Id, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("http")]
    [InlineData("api")]
    [InlineData("malformed")]
    public async Task RealPrometheusPartialFailureRetainsEntireCriticalSnapshotAndOriginalSuccessTime(string failure)
    {
        using var handler = new MetricsHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://prometheus.test/") };
        await using var fixture = await Fixture.Create(configure: services => RegisterRealHealth(services, http));
        fixture.Clock.Now = DateTimeOffset.UtcNow;
        await fixture.Validate();
        var first = await fixture.Read();
        first.Snapshot.Health.Should().NotBeNull();
        first.Snapshot.Health?.MemoryUsagePercent.Should().Be(95);
        first.Snapshot.Health?.OverallStatus.Should().Be(AgentHealthStatus.Critical);
        first.Snapshot.MetricsFailure.Should().BeNull();

        handler.MemoryFailure = failure;
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(300);
        await fixture.Validate();
        var retained = await fixture.Read();
        retained.Snapshot.Health.Should().BeEquivalentTo(first.Snapshot.Health);
        retained.Snapshot.MetricsSuccessUtc.Should().Be(first.Snapshot.MetricsSuccessUtc);
        retained.Snapshot.MetricsAttemptUtc.Should().Be(fixture.Clock.Now);
        retained.Snapshot.MetricsFailure.Should().Be("Prometheus/exporter metrics unavailable.");
        retained.MetricsIsStale.Should().BeFalse(); // Errors are visible immediately, but staleness still waits for expiry.
        retained.Connectivity.Should().Be(DeviceConnectivity.Online);
        handler.Queries.Should().HaveCount(8); // Real up/CPU/memory/disk HTTP queries on both cycles.
    }

    [Fact]
    public async Task RealPrometheusSuccessfulEmptyUnsupportedMetricIsNotAnUpstreamFailure()
    {
        using var handler = new MetricsHandler { EmptyMemory = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://prometheus.test/") };
        await using var fixture = await Fixture.Create(configure: services => RegisterRealHealth(services, http));
        fixture.Clock.Now = DateTimeOffset.UtcNow;
        await fixture.Validate();
        var row = await fixture.Read();
        row.Snapshot.Health?.CpuUsagePercent.Should().Be(20);
        row.Snapshot.Health?.MemoryUsagePercent.Should().BeNull();
        row.Snapshot.Health?.DiskUsagePercent.Should().Be(30);
        row.Snapshot.MetricsSuccessUtc.Should().NotBeNull();
        row.Snapshot.MetricsFailure.Should().BeNull();
        row.MetricsIsStale.Should().BeFalse();
    }

    [Fact]
    public async Task RealPrometheusNoExporterRetainsPriorMetricsWithoutMakingAgentOffline()
    {
        using var handler = new MetricsHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://prometheus.test/") };
        await using var fixture = await Fixture.Create(configure: services => RegisterRealHealth(services, http));
        fixture.Clock.Now = DateTimeOffset.UtcNow;
        await fixture.Validate();
        var first = await fixture.Read();
        handler.NoExporter = true;
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(300);
        await fixture.Validate();
        var retained = await fixture.Read();
        retained.Snapshot.Health.Should().BeEquivalentTo(first.Snapshot.Health);
        retained.Snapshot.MetricsSuccessUtc.Should().Be(first.Snapshot.MetricsSuccessUtc);
        retained.Snapshot.MetricsFailure.Should().NotBeNull();
        retained.Connectivity.Should().Be(DeviceConnectivity.Online);
        handler.Queries.Should().HaveCount(5); // Only `up` is queried when no exporter exists.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledHealthOrPrometheusDoesNotCertifyMetricsOrQueryHttp(bool disableIntegration)
    {
        using var handler = new MetricsHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://prometheus.test/") };
        await using var fixture = await Fixture.Create(configure: services =>
        {
            RegisterRealHealth(services, http, enabled: disableIntegration);
            if (disableIntegration)
                services.AddHealthMonitoring(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Prometheus:Enabled"] = "false" }).Build());
        });
        await fixture.Validate();
        var row = await fixture.Read();
        row.Snapshot.MetricsSuccessUtc.Should().BeNull();
        row.Snapshot.Health.Should().BeNull();
        row.Snapshot.MetricsFailure.Should().NotBeNull();
        row.Connectivity.Should().Be(DeviceConnectivity.Online);
        handler.Queries.Should().BeEmpty();
    }

    private static async Task CancelSaturatedCycle(Fixture fixture, Task saturated)
    {
        using var budget = new CancellationTokenSource();
        var cycle = fixture.Validate(budget.Token);
        try { await saturated.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally
        {
            budget.Cancel();
            await ((Func<Task>)(() => cycle)).Should().ThrowAsync<OperationCanceledException>();
        }
    }

    private static void RegisterRealHealth(IServiceCollection services, HttpClient http, bool enabled = true)
    {
        var options = Options.Create(new PrometheusOptions { Enabled = true });
        services.AddSingleton(options);
        services.AddSingleton(Options.Create(new AgentHealthOptions { Enabled = enabled }));
        services.AddSingleton(new PrometheusClient(http, options, NullLogger<PrometheusClient>.Instance));
        services.AddScoped<IAgentHealthService, AgentHealthService>();
    }

    private sealed class MetricsHandler : HttpMessageHandler
    {
        public string? MemoryFailure { get; set; }
        public bool EmptyMemory { get; init; }
        public bool NoExporter { get; set; }
        public ConcurrentQueue<string> Queries { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing query URI.");
            uri.AbsolutePath.Should().Be("/api/v1/query");
            var query = Uri.UnescapeDataString(uri.Query);
            Queries.Enqueue(query);
            var memory = query.Contains("node_memory_", StringComparison.Ordinal);
            if (memory && MemoryFailure is not null)
                return Task.FromResult(MemoryFailure switch
                {
                    "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("private upstream error") },
                    "api" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new { status = "error", error = "private query error" })
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") }
                });
            var up = query.Contains("query=up{", StringComparison.Ordinal);
            var empty = memory && EmptyMemory || up && NoExporter;
            var value = up ? "1" : memory ? "95" : query.Contains("node_cpu_", StringComparison.Ordinal)
                ? MemoryFailure is null ? "20" : "10"
                : MemoryFailure is null ? "30" : "40";
            // Authentic Prometheus vector wire shape, not serialized domain model objects.
            var result = new[] { new { metric = new Dictionary<string, string>(), value = new object[] { 1700000000, value } } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { status = "success", data = new { resultType = "vector", result = empty ? [] : result } })
            });
        }
    }
}
