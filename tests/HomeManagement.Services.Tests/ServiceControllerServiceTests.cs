using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HomeManagement.Services.Tests;

public class ServiceControllerServiceTests
{
    private readonly IRemoteExecutor _executor = Substitute.For<IRemoteExecutor>();
    private readonly IServiceSnapshotRepository _snapshotRepo = Substitute.For<IServiceSnapshotRepository>();
    private readonly ICorrelationContext _correlation = Substitute.For<ICorrelationContext>();
    private readonly LinuxServiceStrategy _linuxStrategy = new();
    private readonly WindowsServiceStrategy _windowsStrategy = new();
    private readonly ServiceControllerService _sut;

    private static readonly Hostname TestHost = Hostname.Create("test-host");
    private static readonly ServiceName TestService = ServiceName.Create("nginx");

    public ServiceControllerServiceTests()
    {
        _correlation.CorrelationId.Returns("test-correlation");
        _sut = new ServiceControllerService(
            _executor, _snapshotRepo, _correlation,
            NullLogger<ServiceControllerService>.Instance, _linuxStrategy, _windowsStrategy);
    }

    private static MachineTarget LinuxTarget(Guid? id = null) => new(
        id ?? Guid.NewGuid(), TestHost, OsType.Linux,
        MachineConnectionMode.Agentless, TransportProtocol.Ssh, 22, Guid.Empty);

    private static MachineTarget WindowsTarget(Guid? id = null) => new(
        id ?? Guid.NewGuid(), TestHost, OsType.Windows,
        MachineConnectionMode.Agentless, TransportProtocol.WinRM, 5986, Guid.Empty);

    // ── GetStatusAsync ──

    [Fact]
    public async Task GetStatusAsync_Linux_ExecutesStatusCommandAndParsesOutput()
    {
        var target = LinuxTarget();
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(0, "ActiveState=active\nSubState=running\nMainPID=1234\nLoadState=loaded\nUnitFileState=enabled\nDescription=A high performance web server", "", TimeSpan.FromMilliseconds(100), false));

        var result = await _sut.GetStatusAsync(target, TestService);

        result.Name.Should().Be(TestService);
        result.State.Should().Be(ServiceState.Running);
        await _executor.Received(1).ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatusAsync_RecordsSnapshotToRepository()
    {
        var target = LinuxTarget();
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(0, "● nginx.service - A high performance web server\n   Active: active (running) since Mon; 5d ago\n Main PID: 1234 (nginx)", "", TimeSpan.FromMilliseconds(100), false));

        await _sut.GetStatusAsync(target, TestService);

        await _snapshotRepo.Received(1).AddAsync(Arg.Any<ServiceSnapshot>(), Arg.Any<CancellationToken>());
        await _snapshotRepo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── ListServicesAsync ──

    [Fact]
    public async Task ListServicesAsync_Linux_ParsesMultipleServices()
    {
        var target = LinuxTarget();
        var output = "  nginx.service                      loaded active   running  A high performance web server\n" +
                     "  sshd.service                       loaded active   running  OpenSSH server daemon\n";
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(0, output, "", TimeSpan.FromMilliseconds(100), false));

        var result = await _sut.ListServicesAsync(target);

        result.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ListServicesAsync_NonZeroExitCode_ReturnsEmpty()
    {
        var target = LinuxTarget();
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(1, "", "error", TimeSpan.FromMilliseconds(100), false));

        var result = await _sut.ListServicesAsync(target);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListServicesAsync_WithNamePatternFilter_FiltersResults()
    {
        var target = LinuxTarget();
        var output = "  nginx.service                      loaded active   running  A high performance web server\n" +
                     "  sshd.service                       loaded active   running  OpenSSH server daemon\n";
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(0, output, "", TimeSpan.FromMilliseconds(100), false));

        var filter = new ServiceFilter(NamePattern: "nginx");
        var result = await _sut.ListServicesAsync(target, filter);

        result.Should().ContainSingle();
    }

    // ── ListServicesStreamAsync ──

    [Fact]
    public async Task ListServicesStreamAsync_YieldsAllServices()
    {
        var target = LinuxTarget();
        var output = "  nginx.service                      loaded active   running  A high performance web server\n" +
                     "  sshd.service                       loaded active   running  OpenSSH server daemon\n";
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(0, output, "", TimeSpan.FromMilliseconds(100), false));

        var items = new List<ServiceInfo>();
        await foreach (var svc in _sut.ListServicesStreamAsync(target))
            items.Add(svc);

        items.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    // ── ControlAsync ──

    [Fact]
    public async Task ControlAsync_SuccessfulAction_ReturnsSuccessResult()
    {
        var target = LinuxTarget();
        // First call: control command, second call: status check
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(
                new RemoteResult(0, "", "", TimeSpan.FromMilliseconds(100), false),
                new RemoteResult(0, "● nginx.service - Nginx\n   Active: active (running) since Mon;\n Main PID: 1234 (nginx)", "", TimeSpan.FromMilliseconds(100), false));

        var result = await _sut.ControlAsync(target, TestService, ServiceAction.Restart);

        result.Success.Should().BeTrue();
        result.Action.Should().Be(ServiceAction.Restart);
    }

    [Fact]
    public async Task ControlAsync_FailedAction_ReturnsErrorMessage()
    {
        var target = LinuxTarget();
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(
                new RemoteResult(1, "", "Permission denied", TimeSpan.FromMilliseconds(100), false),
                new RemoteResult(0, "● nginx.service - Nginx\n   Active: inactive (dead)\n", "", TimeSpan.FromMilliseconds(100), false));

        var result = await _sut.ControlAsync(target, TestService, ServiceAction.Start);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Permission denied");
    }

    // ── BulkControlAsync ──

    [Fact]
    public async Task BulkControlAsync_ExecutesOnAllTargets()
    {
        var targets = new List<MachineTarget> { LinuxTarget(), LinuxTarget() };
        _executor.ExecuteAsync(Arg.Any<MachineTarget>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(
                new RemoteResult(0, "", "", TimeSpan.FromMilliseconds(100), false),
                new RemoteResult(0, "● nginx.service - Nginx\n   Active: active (running)\n Main PID: 1 (nginx)", "", TimeSpan.FromMilliseconds(100), false));

        var results = await _sut.BulkControlAsync(targets, TestService, ServiceAction.Stop);

        results.Should().HaveCount(2);
    }

    // ── GetStrategy ──

    [Fact]
    public async Task GetStatusAsync_UnsupportedOs_ThrowsNotSupported()
    {
        var target = new MachineTarget(Guid.NewGuid(), TestHost, (OsType)99,
            MachineConnectionMode.Agentless, TransportProtocol.Ssh, 22, Guid.Empty);

        var act = () => _sut.GetStatusAsync(target, TestService);

        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
