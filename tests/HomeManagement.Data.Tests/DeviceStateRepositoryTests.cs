using FluentAssertions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Data.Entities;
using HomeManagement.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Tests;

public sealed class DeviceStateRepositoryTests
{
    [Fact]
    public async Task MigrationAndEvidenceSurviveClosedDatabaseAndNewContextsAndSoftDeletionHidesEvidence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hm-device-state-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<HomeManagementDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 4, 16, 0, 0, TimeSpan.Zero);
        var snapshot = new DeviceStateSnapshot
        {
            MachineId = id,
            AgentAttemptUtc = now.AddSeconds(1),
            AgentSuccessUtc = now,
            LastKnownConnected = true,
            LastHeartbeatUtc = now.AddMinutes(-1),
            AgentFailure = "Gateway validation unavailable.",
            MetricsAttemptUtc = now,
            MetricsSuccessUtc = now,
            Health = new() { Id = id, IsOnline = true, CpuUsagePercent = 23.5 },
            ManagedObservation = new(Guid.NewGuid(), id, "bound-agent", 7, 3, Guid.NewGuid(), true, now, now.AddMinutes(-1)),
            MetricsFailure = "Prometheus/exporter metrics unavailable."
        };
        try
        {
            await using (var db = new HomeManagementDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.Machines.Add(new MachineEntity { Id = id, Hostname = "node" });
                await db.SaveChangesAsync();
                await new DeviceStateRepository(db).UpsertAsync(snapshot);
                // Repositories stage changes until the unit of work/context commits.
                await using (var beforeCommit = new HomeManagementDbContext(options))
                    (await new DeviceStateRepository(beforeCommit).GetAsync(id)).Should().BeNull();
                await db.SaveChangesAsync();
            }
            await using (var restarted = new HomeManagementDbContext(options))
            {
                var stored = await new DeviceStateRepository(restarted).GetAsync(id);
                stored.Should().BeEquivalentTo(snapshot);
                (await new DeviceStateRepository(restarted).GetForMachinesAsync([id])).Should().ContainKey(id);
                var machine = await restarted.Machines.SingleAsync();
                machine.IsDeleted = true;
                await restarted.SaveChangesAsync();
            }
            await using (var reader = new HomeManagementDbContext(options))
            {
                var repository = new DeviceStateRepository(reader);
                (await repository.GetAsync(id)).Should().BeNull();
                (await repository.GetForMachinesAsync([id])).Should().BeEmpty();
                await repository.UpsertAsync(snapshot with { LastKnownConnected = false });
                await reader.SaveChangesAsync();
                (await reader.DeviceStates.IgnoreQueryFilters().SingleAsync()).LastKnownConnected.Should().BeTrue();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
