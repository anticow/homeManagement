using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using HomeManagement.Data;
using HomeManagement.Data.Repositories;

namespace HomeManagement.Integration.Tests;

[Collection("SqlServer")]
public sealed class MachineRepositoryIntegrationTests : IDisposable
{
    private readonly HomeManagementDbContext _context;
    private readonly MachineRepository _sut;

    public MachineRepositoryIntegrationTests(SqlServerFixture fixture)
    {
        _context = fixture.CreateContext();
        _sut = new MachineRepository(_context);
    }

    [Fact]
    public async Task AddAsync_And_GetByIdAsync_RoundTrip()
    {
        var machine = CreateMachine();

        await _sut.AddAsync(machine);
        await _context.SaveChangesAsync();

        var retrieved = await _sut.GetByIdAsync(machine.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Hostname.Value.Should().Be(machine.Hostname.Value);
        retrieved.OsType.Should().Be(OsType.Linux);
    }

    [Fact]
    public async Task SoftDeleteAsync_ExcludesFromDefaultQuery()
    {
        var machine = CreateMachine();
        await _sut.AddAsync(machine);
        await _context.SaveChangesAsync();

        await _sut.SoftDeleteAsync(machine.Id);
        await _context.SaveChangesAsync();

        var afterDelete = await _sut.GetByIdAsync(machine.Id);
        afterDelete.Should().BeNull("soft-deleted machines are excluded by global query filter");
    }

    [Fact]
    public async Task SoftDeleteRangeAsync_MarksMultipleAsDeleted()
    {
        var m1 = CreateMachine();
        var m2 = CreateMachine();
        await _sut.AddAsync(m1);
        await _sut.AddAsync(m2);
        await _context.SaveChangesAsync();

        await _sut.SoftDeleteRangeAsync([m1.Id, m2.Id]);
        await _context.SaveChangesAsync();

        (await _sut.GetByIdAsync(m1.Id)).Should().BeNull();
        (await _sut.GetByIdAsync(m2.Id)).Should().BeNull();
    }

    [Fact]
    public async Task AddRangeAsync_PersistsMultipleMachines()
    {
        var machines = new[] { CreateMachine(), CreateMachine(), CreateMachine() };
        await _sut.AddRangeAsync(machines);
        await _context.SaveChangesAsync();

        foreach (var m in machines)
        {
            var retrieved = await _sut.GetByIdAsync(m.Id);
            retrieved.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task QueryAsync_FiltersByOsType()
    {
        var linux = CreateMachine(OsType.Linux);
        var windows = CreateMachine(OsType.Windows);
        await _sut.AddAsync(linux);
        await _sut.AddAsync(windows);
        await _context.SaveChangesAsync();

        var query = new MachineQuery(OsType: OsType.Linux, Page: 1, PageSize: 100);
        var result = await _sut.QueryAsync(query);

        result.Items.Should().Contain(m => m.Id == linux.Id);
        result.Items.Should().NotContain(m => m.Id == windows.Id);
    }

    [Fact]
    public async Task QueryAsync_IncludeDeleted_ShowsSoftDeleted()
    {
        var machine = CreateMachine();
        await _sut.AddAsync(machine);
        await _context.SaveChangesAsync();
        await _sut.SoftDeleteAsync(machine.Id);
        await _context.SaveChangesAsync();

        var query = new MachineQuery(IncludeDeleted: true, Page: 1, PageSize: 100);
        var result = await _sut.QueryAsync(query);

        result.Items.Should().Contain(m => m.Id == machine.Id);
    }

    [Fact]
    public async Task QueryAsync_Pagination_ReturnsCorrectPage()
    {
        for (int i = 0; i < 5; i++)
            await _sut.AddAsync(CreateMachine());
        await _context.SaveChangesAsync();

        var page1 = await _sut.QueryAsync(new MachineQuery(Page: 1, PageSize: 2));
        var page2 = await _sut.QueryAsync(new MachineQuery(Page: 2, PageSize: 2));

        page1.Items.Should().HaveCount(2);
        page2.Items.Should().HaveCount(2);
    }

    private static Machine CreateMachine(OsType os = OsType.Linux)
    {
        var name = $"test-{Guid.NewGuid():N}".Substring(0, 20);
        Hostname.TryCreate(name, out var hostname, out _);
        var now = DateTime.UtcNow;
        return new Machine(
            Guid.NewGuid(), hostname!, null, [], os, "Ubuntu 22.04",
            MachineConnectionMode.Agentless, TransportProtocol.Ssh, 22,
            Guid.Empty, MachineState.Online,
            new Dictionary<string, string>().AsReadOnly(),
            null, now, now, now);
    }

    public void Dispose() => _context.Dispose();
}
