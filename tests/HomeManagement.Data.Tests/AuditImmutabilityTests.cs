using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Data;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Data.Tests;

public sealed class AuditImmutabilityTests : IDisposable
{
    private readonly HomeManagementDbContext _context;

    public AuditImmutabilityTests()
    {
        var options = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _context = new HomeManagementDbContext(options);
        _context.Database.OpenConnection();
        _context.Database.EnsureCreated();
    }

    [Fact]
    public void SaveChanges_AddAuditEvent_Succeeds()
    {
        var audit = CreateAuditEvent();
        _context.AuditEvents.Add(audit);

        var saved = _context.SaveChanges();
        saved.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SaveChangesAsync_AddAuditEvent_Succeeds()
    {
        var audit = CreateAuditEvent();
        _context.AuditEvents.Add(audit);

        var saved = await _context.SaveChangesAsync();
        saved.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SaveChanges_ModifyAuditEvent_ThrowsInvalidOperation()
    {
        var audit = CreateAuditEvent();
        _context.AuditEvents.Add(audit);
        _context.SaveChanges();

        audit.Action = AuditAction.MachineAdded;

        var act = () => _context.SaveChanges();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*append-only*");
    }

    [Fact]
    public void SaveChanges_DeleteAuditEvent_ThrowsInvalidOperation()
    {
        var audit = CreateAuditEvent();
        _context.AuditEvents.Add(audit);
        _context.SaveChanges();

        _context.AuditEvents.Remove(audit);

        var act = () => _context.SaveChanges();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*append-only*");
    }

    [Fact]
    public async Task SaveChangesAsync_ModifyAuditEvent_ThrowsInvalidOperation()
    {
        var audit = CreateAuditEvent();
        _context.AuditEvents.Add(audit);
        await _context.SaveChangesAsync();

        audit.Action = AuditAction.MachineAdded;

        var act = () => _context.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*append-only*");
    }

    [Fact]
    public async Task SaveChangesAsync_DeleteAuditEvent_ThrowsInvalidOperation()
    {
        var audit = CreateAuditEvent();
        _context.AuditEvents.Add(audit);
        await _context.SaveChangesAsync();

        _context.AuditEvents.Remove(audit);

        var act = () => _context.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*append-only*");
    }

    [Fact]
    public void SaveChanges_AfterViolation_ContextNotPoisoned()
    {
        var audit = CreateAuditEvent();
        _context.AuditEvents.Add(audit);
        _context.SaveChanges();

        // Attempt modification (should throw)
        audit.Action = AuditAction.MachineAdded;
        var act = () => _context.SaveChanges();
        act.Should().Throw<InvalidOperationException>();

        // Context should still be usable — the entry was reset to Unchanged
        var newAudit = CreateAuditEvent();
        _context.AuditEvents.Add(newAudit);
        var saved = _context.SaveChanges();
        saved.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SaveChanges_ModifyNonAuditEntity_Succeeds()
    {
        var machine = new MachineEntity
        {
            Id = Guid.NewGuid(),
            Hostname = "test-machine",
            OsType = OsType.Linux,
            State = MachineState.Online,
            ConnectionMode = MachineConnectionMode.Agent,
            Protocol = TransportProtocol.Agent,
            CreatedUtc = DateTime.UtcNow
        };

        _context.Machines.Add(machine);
        _context.SaveChanges();

        machine.State = MachineState.Offline;
        var saved = _context.SaveChanges();
        saved.Should().BeGreaterThan(0);
    }

    private static AuditEventEntity CreateAuditEvent() => new()
    {
        EventId = Guid.NewGuid(),
        TimestampUtc = DateTime.UtcNow,
        Action = AuditAction.PatchInstallCompleted,
        Outcome = AuditOutcome.Success,
        CorrelationId = Guid.NewGuid().ToString("N"),
        ActorIdentity = "test-user"
    };

    public void Dispose() => _context.Dispose();
}
