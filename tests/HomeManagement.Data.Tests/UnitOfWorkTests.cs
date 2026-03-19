using FluentAssertions;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data;
using HomeManagement.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace HomeManagement.Data.Tests;

/// <summary>
/// Tests for <see cref="UnitOfWork"/> — verifies HIGH-10 fix.
/// </summary>
public sealed class UnitOfWorkTests : IDisposable
{
    private readonly HomeManagementDbContext _context;

    public UnitOfWorkTests()
    {
        var options = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _context = new HomeManagementDbContext(options);
        _context.Database.OpenConnection();
        _context.Database.EnsureCreated();
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Constructor_ExposesAllRepositories()
    {
        var machines = Substitute.For<IMachineRepository>();
        var patchHistory = Substitute.For<IPatchHistoryRepository>();
        var auditEvents = Substitute.For<IAuditEventRepository>();
        var jobs = Substitute.For<IJobRepository>();
        var snapshots = Substitute.For<IServiceSnapshotRepository>();

        var uow = new UnitOfWork(_context, machines, patchHistory, auditEvents, jobs, snapshots);

        uow.Machines.Should().BeSameAs(machines);
        uow.PatchHistory.Should().BeSameAs(patchHistory);
        uow.AuditEvents.Should().BeSameAs(auditEvents);
        uow.Jobs.Should().BeSameAs(jobs);
        uow.ServiceSnapshots.Should().BeSameAs(snapshots);
    }

    [Fact]
    public async Task SaveChangesAsync_DelegatesToDbContext()
    {
        var uow = new UnitOfWork(
            _context,
            Substitute.For<IMachineRepository>(),
            Substitute.For<IPatchHistoryRepository>(),
            Substitute.For<IAuditEventRepository>(),
            Substitute.For<IJobRepository>(),
            Substitute.For<IServiceSnapshotRepository>());

        // Should not throw on empty save
        await uow.Invoking(u => u.SaveChangesAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var uow = new UnitOfWork(
            _context,
            Substitute.For<IMachineRepository>(),
            Substitute.For<IPatchHistoryRepository>(),
            Substitute.For<IAuditEventRepository>(),
            Substitute.For<IJobRepository>(),
            Substitute.For<IServiceSnapshotRepository>());

        uow.Invoking(u => u.Dispose()).Should().NotThrow();
    }
}
