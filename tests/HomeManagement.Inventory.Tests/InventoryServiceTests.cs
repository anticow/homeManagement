using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HomeManagement.Inventory.Tests;

public sealed class InventoryServiceTests
{
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IMachineRepository _machineRepo = Substitute.For<IMachineRepository>();
    private readonly IRemoteExecutor _executor = Substitute.For<IRemoteExecutor>();
    private readonly ICorrelationContext _correlation = Substitute.For<ICorrelationContext>();
    private readonly InventoryService _sut;

    public InventoryServiceTests()
    {
        _correlation.CorrelationId.Returns("test-correlation");
        _uow.Machines.Returns(_machineRepo);
        _sut = new InventoryService(_uow, _executor, _correlation, NullLogger<InventoryService>.Instance);
    }

    [Fact]
    public async Task AddAsync_PersistsMachineAndSaves()
    {
        var request = new MachineCreateRequest(
            Hostname.Create("test-host"), null, OsType.Linux,
            MachineConnectionMode.Agentless, TransportProtocol.Ssh, 22, Guid.Empty);

        var result = await _sut.AddAsync(request);

        result.Should().NotBeNull();
        result.Hostname.Value.Should().Be("test-host");
        result.OsType.Should().Be(OsType.Linux);
        await _machineRepo.Received(1).AddAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_ThrowsWhenNotFound()
    {
        _machineRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        var act = () => _sut.UpdateAsync(Guid.NewGuid(), new MachineUpdateRequest());
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task UpdateAsync_UpdatesAndSaves()
    {
        var id = Guid.NewGuid();
        var existing = CreateMachine(id);
        _machineRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await _sut.UpdateAsync(id, new MachineUpdateRequest { State = MachineState.Maintenance });

        result.State.Should().Be(MachineState.Maintenance);
        await _machineRepo.Received(1).UpdateAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_SoftDeletesAndSaves()
    {
        var id = Guid.NewGuid();

        await _sut.RemoveAsync(id);

        await _machineRepo.Received(1).SoftDeleteAsync(id, Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BatchRemoveAsync_SoftDeletesRangeAndSaves()
    {
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        await _sut.BatchRemoveAsync(ids);

        await _machineRepo.Received(1).SoftDeleteRangeAsync(ids, Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_DelegatesToRepository()
    {
        var id = Guid.NewGuid();
        var machine = CreateMachine(id);
        _machineRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(machine);

        var result = await _sut.GetAsync(id);

        result.Should().NotBeNull();
        result!.Id.Should().Be(id);
    }

    [Fact]
    public async Task QueryAsync_DelegatesToRepository()
    {
        var query = new MachineQuery(Page: 1, PageSize: 10);
        var expected = new PagedResult<Machine>([], 0, 1, 10);
        _machineRepo.QueryAsync(query, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.QueryAsync(query);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task RefreshMetadataAsync_ThrowsWhenNotFound()
    {
        _machineRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        var act = () => _sut.RefreshMetadataAsync(Guid.NewGuid());
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RefreshMetadataAsync_ExecutesRemoteCommand()
    {
        var id = Guid.NewGuid();
        var machine = CreateMachine(id);
        _machineRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(machine);
        _executor.ExecuteAsync(Arg.Any<MachineTarget>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(0, "4,8589934592,x86_64", "", TimeSpan.FromSeconds(1), false));

        var result = await _sut.RefreshMetadataAsync(id);

        result.Hardware.Should().NotBeNull();
        result.Hardware!.CpuCores.Should().Be(4);
        result.State.Should().Be(MachineState.Online);
        await _machineRepo.Received(1).UpdateAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportAsync_Json_WritesToStream()
    {
        var query = new MachineQuery(Page: 1, PageSize: 10000);
        _machineRepo.QueryAsync(Arg.Any<MachineQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Machine>([], 0, 1, 10000));

        using var stream = new MemoryStream();
        await _sut.ExportAsync(query, stream, ExportFormat.Json);

        stream.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExportAsync_Csv_WritesHeaderAndData()
    {
        var machine = CreateMachine(Guid.NewGuid());
        var query = new MachineQuery(Page: 1, PageSize: 10000);
        _machineRepo.QueryAsync(Arg.Any<MachineQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Machine>([machine], 1, 1, 10000));

        using var stream = new MemoryStream();
        await _sut.ExportAsync(query, stream, ExportFormat.Csv);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var csv = await reader.ReadToEndAsync();
        csv.Should().Contain("Hostname,OsType,Protocol,Port,State,ConnectionMode");
        csv.Should().Contain("test-host");
    }

    private static Machine CreateMachine(Guid id)
    {
        var now = DateTime.UtcNow;
        return new Machine(id, Hostname.Create("test-host"), null, [], OsType.Linux, "Ubuntu 22.04",
            MachineConnectionMode.Agentless, TransportProtocol.Ssh, 22, Guid.Empty,
            MachineState.Online, new Dictionary<string, string>().AsReadOnly(), null, now, now, now);
    }

    // ── ImportAsync ──

    [Fact]
    public async Task ImportAsync_ValidCsv_AddsMachines()
    {
        var csv = "Hostname,OsType,Protocol,Port,Fqdn\nserver1,Linux,Ssh,22,\nserver2,Windows,WinRM,5986,\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        await _sut.ImportAsync(stream);

        await _machineRepo.Received(2).AddAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_EmptyStream_DoesNothing()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(""));

        await _sut.ImportAsync(stream);

        await _machineRepo.DidNotReceive().AddAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_InvalidRows_AreSkipped()
    {
        var csv = "Hostname,OsType,Protocol,Port,Fqdn\n,BadOs,Ssh,22,\nserver1,Linux,Ssh,22,\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        await _sut.ImportAsync(stream);

        // Only server1 should be added; the first row has empty hostname and bad OsType
        await _machineRepo.Received(1).AddAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportAsync_ShortRows_AreSkipped()
    {
        var csv = "Hostname,OsType,Protocol,Port,Fqdn\ntoo,few,cols\nserver1,Linux,Ssh,22,\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        await _sut.ImportAsync(stream);

        await _machineRepo.Received(1).AddAsync(Arg.Any<Machine>(), Arg.Any<CancellationToken>());
    }

    // ── DiscoverAsync ──

    [Fact]
    public async Task DiscoverAsync_ReachableHosts_ReturnsDiscoveredMachines()
    {
        _executor.TestConnectionAsync(Arg.Any<MachineTarget>(), Arg.Any<CancellationToken>())
            .Returns(new ConnectionTestResult(true, OsType.Linux, "Ubuntu", TimeSpan.FromMilliseconds(5), null, null));

        var result = await _sut.DiscoverAsync(CidrRange.Create("192.168.1.0/30"));

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(m => m.State == MachineState.Online);
    }

    [Fact]
    public async Task DiscoverAsync_NoReachableHosts_ReturnsEmpty()
    {
        _executor.TestConnectionAsync(Arg.Any<MachineTarget>(), Arg.Any<CancellationToken>())
            .Returns(new ConnectionTestResult(false, null, null, TimeSpan.Zero, null, "Connection refused"));

        var result = await _sut.DiscoverAsync(CidrRange.Create("10.0.0.0/30"));

        result.Should().BeEmpty();
    }

    // ── RefreshMetadataAsync edge cases ──

    [Fact]
    public async Task RefreshMetadataAsync_FailedCommand_KeepsExistingHardware()
    {
        var id = Guid.NewGuid();
        var machine = CreateMachine(id);
        _machineRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(machine);
        _executor.ExecuteAsync(Arg.Any<MachineTarget>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(1, "", "Connection refused", TimeSpan.FromSeconds(1), false));

        var result = await _sut.RefreshMetadataAsync(id);

        result.Hardware.Should().Be(machine.Hardware);
    }
}
