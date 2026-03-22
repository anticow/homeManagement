using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Data;
using HomeManagement.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace HomeManagement.Integration.Tests;

[Collection("SqlServer")]
public sealed class AuditImmutabilitySqlServerTests : IDisposable
{
    private readonly HomeManagementDbContext _context;

    public AuditImmutabilitySqlServerTests(SqlServerFixture fixture)
    {
        _context = fixture.CreateContext();
    }

    [Fact]
    public async Task AddAuditEvent_Succeeds_OnSqlServer()
    {
        var audit = CreateAudit();
        _context.AuditEvents.Add(audit);

        var saved = await _context.SaveChangesAsync();
        saved.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ModifyAuditEvent_ThrowsInvalidOperation_OnSqlServer()
    {
        var audit = CreateAudit();
        _context.AuditEvents.Add(audit);
        _context.SaveChanges();

        audit.Action = AuditAction.MachineAdded;

        var act = () => _context.SaveChanges();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*append-only*");
    }

    [Fact]
    public void DeleteAuditEvent_ThrowsInvalidOperation_OnSqlServer()
    {
        var audit = CreateAudit();
        _context.AuditEvents.Add(audit);
        _context.SaveChanges();

        _context.AuditEvents.Remove(audit);

        var act = () => _context.SaveChanges();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*append-only*");
    }

    [Fact]
    public async Task SoftDeletedMachines_AreFilteredByDefault_OnSqlServer()
    {
        var entity = new MachineEntity
        {
            Id = Guid.NewGuid(),
            Hostname = $"test-{Guid.NewGuid():N}"[..20],
            OsType = OsType.Linux,
            OsVersion = "Ubuntu 22.04",
            ConnectionMode = MachineConnectionMode.Agentless,
            Protocol = TransportProtocol.Ssh,
            Port = 22,
            State = MachineState.Online,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            LastContactUtc = DateTime.UtcNow,
            IsDeleted = true
        };

        _context.Machines.Add(entity);
        await _context.SaveChangesAsync();

        var result = await _context.Machines.FirstOrDefaultAsync(m => m.Id == entity.Id);
        result.Should().BeNull("global soft-delete filter should exclude deleted machines");

        var withFilter = await _context.Machines
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Id == entity.Id);
        withFilter.Should().NotBeNull();
    }

    private static AuditEventEntity CreateAudit() => new()
    {
        EventId = Guid.NewGuid(),
        TimestampUtc = DateTime.UtcNow,
        Action = AuditAction.VaultUnlocked,
        ActorIdentity = "test-user",
        Detail = "Integration test",
        Outcome = AuditOutcome.Success,
        EventHash = Convert.ToBase64String(new byte[32])
    };

    public void Dispose() => _context.Dispose();
}
