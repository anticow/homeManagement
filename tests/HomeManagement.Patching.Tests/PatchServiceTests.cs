using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Abstractions.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HomeManagement.Patching.Tests;

public sealed class PatchServiceTests
{
    private readonly IRemoteExecutor _executor = Substitute.For<IRemoteExecutor>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IPatchHistoryRepository _historyRepo = Substitute.For<IPatchHistoryRepository>();
    private readonly ICorrelationContext _correlation = Substitute.For<ICorrelationContext>();
    private readonly PatchService _sut;

    public PatchServiceTests()
    {
        _correlation.CorrelationId.Returns("test-corr");
        _uow.PatchHistory.Returns(_historyRepo);
        _sut = new PatchService(
            _executor, _uow, _correlation, NullLogger<PatchService>.Instance,
            new LinuxPatchStrategy(), new WindowsPatchStrategy());
    }

    [Fact]
    public async Task DetectAsync_Linux_ParsesAptOutput()
    {
        var target = CreateTarget(OsType.Linux);
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(0, "vim/jammy-updates 9.0.0 amd64 [upgradable from: 8.2.0]\ncurl/jammy-security 7.81.0 amd64 [upgradable from: 7.80.0]", "", TimeSpan.FromSeconds(2), false));

        var patches = await _sut.DetectAsync(target);

        patches.Should().HaveCount(2);
        patches[0].PatchId.Should().Be("vim");
        patches[1].PatchId.Should().Be("curl");
    }

    [Fact]
    public async Task DetectAsync_ReturnsEmpty_OnNonZeroExitCode()
    {
        var target = CreateTarget(OsType.Linux);
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(1, "", "error", TimeSpan.FromSeconds(1), false));

        var patches = await _sut.DetectAsync(target);

        patches.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_RecordsHistoryForEachPatch()
    {
        var target = CreateTarget(OsType.Linux);
        var patches = new List<PatchInfo>
        {
            new("vim", "vim 9.0", PatchSeverity.Moderate, PatchCategory.Security, "desc", 0, false, DateTime.UtcNow),
            new("curl", "curl 7.81", PatchSeverity.Critical, PatchCategory.Security, "desc", 0, false, DateTime.UtcNow)
        };

        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(0, "done", "", TimeSpan.FromSeconds(5), false));

        var result = await _sut.ApplyAsync(target, patches, new PatchOptions());

        result.Successful.Should().Be(1);
        await _historyRepo.Received(2).AddAsync(Arg.Any<PatchHistoryEntry>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHistoryAsync_DelegatesToRepository()
    {
        var machineId = Guid.NewGuid();
        var entries = new List<PatchHistoryEntry>();
        _historyRepo.GetByMachineAsync(machineId, Arg.Any<CancellationToken>()).Returns(entries);

        var result = await _sut.GetHistoryAsync(machineId);

        result.Should().BeSameAs(entries);
    }

    [Fact]
    public async Task GetInstalledAsync_Linux_ParsesDpkgOutput()
    {
        var target = CreateTarget(OsType.Linux);
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(0, "vim 9.0.0 ii\ncurl 7.81.0 ii", "", TimeSpan.FromSeconds(1), false));

        var installed = await _sut.GetInstalledAsync(target);

        installed.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetInstalledAsync_ReturnsEmpty_OnFailure()
    {
        var target = CreateTarget(OsType.Linux);
        _executor.ExecuteAsync(target, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RemoteResult(1, "", "error", TimeSpan.FromSeconds(1), false));

        var installed = await _sut.GetInstalledAsync(target);

        installed.Should().BeEmpty();
    }

    [Fact]
    public void GetStrategy_ThrowsForUnsupportedOs()
    {
        var target = CreateTarget((OsType)999);
        var act = () => _sut.DetectAsync(target);
        act.Should().ThrowAsync<NotSupportedException>();
    }

    private static MachineTarget CreateTarget(OsType os) => new(
        Guid.NewGuid(), Hostname.Create("test-host"), os,
        MachineConnectionMode.Agentless, TransportProtocol.Ssh, 22, Guid.Empty);
}

public sealed class LinuxPatchStrategyTests
{
    private readonly LinuxPatchStrategy _sut = new();

    [Fact]
    public void BuildDetectCommand_ReturnsAptCommand()
    {
        _sut.BuildDetectCommand().Should().Contain("apt list --upgradable");
    }

    [Fact]
    public void ParseDetectOutput_EmptyInput_ReturnsEmpty()
    {
        _sut.ParseDetectOutput("").Should().BeEmpty();
        _sut.ParseDetectOutput(null!).Should().BeEmpty();
    }

    [Fact]
    public void ParseDetectOutput_ParsesValidLines()
    {
        var output = "vim/jammy-security 9.0 amd64 [upgradable from: 8.2]\ncurl/jammy 7.81 amd64";
        var result = _sut.ParseDetectOutput(output);

        result.Should().HaveCount(2);
        result[0].PatchId.Should().Be("vim");
        result[0].Severity.Should().Be(PatchSeverity.Critical);
        result[1].PatchId.Should().Be("curl");
    }

    [Fact]
    public void BuildApplyCommand_WithDryRun_IncludesFlag()
    {
        var patches = new[] { new PatchInfo("vim", "vim", PatchSeverity.Moderate, PatchCategory.Security, "", 0, false, DateTime.UtcNow) };
        var cmd = _sut.BuildApplyCommand(patches, new PatchOptions(DryRun: true));
        cmd.Should().Contain("--dry-run");
    }

    [Fact]
    public void BuildApplyCommand_SanitizesPackageNames()
    {
        var patches = new[] { new PatchInfo("vim;rm -rf /", "bad", PatchSeverity.Moderate, PatchCategory.Security, "", 0, false, DateTime.UtcNow) };
        var cmd = _sut.BuildApplyCommand(patches, new PatchOptions());
        cmd.Should().NotContain(";");
        cmd.Should().NotContain("/");
        cmd.Should().NotContain(" -rf");
    }

    [Fact]
    public void ParseApplyOutput_SuccessExitCode_ReturnsSuccess()
    {
        var result = _sut.ParseApplyOutput(Guid.NewGuid(), "done", "", 0, TimeSpan.FromSeconds(1));
        result.Successful.Should().Be(1);
        result.Failed.Should().Be(0);
    }

    [Fact]
    public void ParseApplyOutput_FailedExitCode_ReturnsFailure()
    {
        var result = _sut.ParseApplyOutput(Guid.NewGuid(), "", "error", 1, TimeSpan.FromSeconds(1));
        result.Failed.Should().Be(1);
    }

    [Fact]
    public void ParseInstalledOutput_ParsesPackages()
    {
        var result = _sut.ParseInstalledOutput("vim 9.0 ii\ncurl 7.81 ii");
        result.Should().HaveCount(2);
    }
}

public sealed class WindowsPatchStrategyTests
{
    private readonly WindowsPatchStrategy _sut = new();

    [Fact]
    public void ParseDetectOutput_ParsesSingleJsonObject()
    {
        var json = "{\"KB\":\"KB12345\",\"Title\":\"Security Update\",\"Size\":1024,\"MsrcSeverity\":\"Critical\"}";
        var result = _sut.ParseDetectOutput(json);

        result.Should().HaveCount(1);
        result[0].PatchId.Should().Be("KB12345");
        result[0].Severity.Should().Be(PatchSeverity.Critical);
    }

    [Fact]
    public void ParseDetectOutput_ParsesJsonArray()
    {
        var json = "[{\"KB\":\"KB111\",\"Title\":\"Patch 1\",\"Size\":100,\"MsrcSeverity\":\"Important\"},{\"KB\":\"KB222\",\"Title\":\"Patch 2\",\"Size\":200,\"MsrcSeverity\":\"Low\"}]";
        var result = _sut.ParseDetectOutput(json);

        result.Should().HaveCount(2);
        result[0].Severity.Should().Be(PatchSeverity.Important);
        result[1].Severity.Should().Be(PatchSeverity.Low);
    }

    [Fact]
    public void ParseDetectOutput_InvalidJson_ReturnsEmpty()
    {
        _sut.ParseDetectOutput("not json").Should().BeEmpty();
    }

    [Fact]
    public void ParseDetectOutput_EmptyInput_ReturnsEmpty()
    {
        _sut.ParseDetectOutput("").Should().BeEmpty();
    }

    [Fact]
    public void BuildApplyCommand_SanitizesKbIds()
    {
        var patches = new[] { new PatchInfo("KB';DROP TABLE--", "bad", PatchSeverity.Critical, PatchCategory.Security, "", 0, false, DateTime.UtcNow) };
        var cmd = _sut.BuildApplyCommand(patches, new PatchOptions());
        // SanitizeKbId keeps only alphanumeric, so injected chars are stripped
        cmd.Should().NotContain(";");
        cmd.Should().NotContain("--");
        cmd.Should().NotContain(" TABLE");
    }

    [Fact]
    public void ParseInstalledOutput_ParsesJsonArray()
    {
        var json = "[{\"HotFixID\":\"KB123\",\"Description\":\"Update\",\"InstalledOn\":\"2024-01-15\"}]";
        var result = _sut.ParseInstalledOutput(json);

        result.Should().HaveCount(1);
        result[0].PatchId.Should().Be("KB123");
    }

    [Fact]
    public void ParseInstalledOutput_EmptyInput_ReturnsEmpty()
    {
        _sut.ParseInstalledOutput("").Should().BeEmpty();
    }
}
