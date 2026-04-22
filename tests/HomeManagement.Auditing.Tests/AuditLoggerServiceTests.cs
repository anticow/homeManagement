using FluentAssertions;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HomeManagement.Auditing.Tests;

public sealed class AuditLoggerServiceTests
{
    private readonly IAuditEventRepository _repo = Substitute.For<IAuditEventRepository>();
    private readonly ISensitiveDataFilter _filter = Substitute.For<ISensitiveDataFilter>();
    private readonly ICorrelationContext _correlation = Substitute.For<ICorrelationContext>();
    private readonly AuditLoggerService _sut;

    // A deterministic 32-byte HMAC key for tests (not a real secret)
    private static readonly byte[] TestHmacKey = new byte[32];
    private static IOptions<AuditOptions> TestOptions => Options.Create(new AuditOptions
    {
        HmacKey = Convert.ToBase64String(TestHmacKey)
    });

    public AuditLoggerServiceTests()
    {
        _correlation.CorrelationId.Returns("test-corr");
        _filter.Redact(Arg.Any<string?>()).Returns(ci => ci.Arg<string?>());
        _filter.RedactProperties(Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(ci => ci.Arg<IReadOnlyDictionary<string, string>?>());
        _repo.GetLastEventHashAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        _sut = new AuditLoggerService(_repo, _filter, _correlation, NullLogger<AuditLoggerService>.Instance, TestOptions);
    }

    [Fact]
    public async Task RecordAsync_PersistsAndSaves()
    {
        var evt = CreateAuditEvent();

        await _sut.RecordAsync(evt);

        await _repo.Received(1).AddAsync(Arg.Any<AuditEvent>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_RedactsSensitiveData()
    {
        _filter.Redact("password=secret123").Returns("[REDACTED]");

        var evt = CreateAuditEvent() with { Detail = "password=secret123" };
        await _sut.RecordAsync(evt);

        await _repo.Received(1).AddAsync(
            Arg.Is<AuditEvent>(e => e.Detail == "[REDACTED]"),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_SetsCorrelationId_WhenMissing()
    {
        var evt = CreateAuditEvent() with { CorrelationId = "" };
        await _sut.RecordAsync(evt);

        await _repo.Received(1).AddAsync(
            Arg.Is<AuditEvent>(e => e.CorrelationId == "test-corr"),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_PreservesExistingCorrelationId()
    {
        var evt = CreateAuditEvent() with { CorrelationId = "existing-id" };
        await _sut.RecordAsync(evt);

        await _repo.Received(1).AddAsync(
            Arg.Is<AuditEvent>(e => e.CorrelationId == "existing-id"),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ChainHash_IsNotEmpty()
    {
        var evt = CreateAuditEvent();
        await _sut.RecordAsync(evt);

        await _repo.Received(1).AddAsync(
            Arg.Any<AuditEvent>(), Arg.Any<string?>(),
            Arg.Is<string>(hash => !string.IsNullOrEmpty(hash)),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ChainHash_IncludesPreviousHash()
    {
        _repo.GetLastEventHashAsync(Arg.Any<CancellationToken>()).Returns("abc123");

        var evt = CreateAuditEvent();
        await _sut.RecordAsync(evt);

        await _repo.Received(1).AddAsync(
            Arg.Any<AuditEvent>(), Arg.Is<string?>(h => h == "abc123"),
            Arg.Is<string>(hash => !string.IsNullOrEmpty(hash)),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordAsync_ChainVersion_IsOne()
    {
        var evt = CreateAuditEvent();
        await _sut.RecordAsync(evt);

        await _repo.Received(1).AddAsync(
            Arg.Any<AuditEvent>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Is<int>(v => v == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ComputeEventHash_IsDeterministic()
    {
        var evt = CreateAuditEvent();
        var hash1 = AuditLoggerService.ComputeEventHash(evt, "prev", TestHmacKey);
        var hash2 = AuditLoggerService.ComputeEventHash(evt, "prev", TestHmacKey);
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeEventHash_DiffersByPreviousHash()
    {
        var evt = CreateAuditEvent();
        var hash1 = AuditLoggerService.ComputeEventHash(evt, null, TestHmacKey);
        var hash2 = AuditLoggerService.ComputeEventHash(evt, "different", TestHmacKey);
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public async Task QueryAsync_DelegatesToRepository()
    {
        var query = new AuditQuery(Page: 1, PageSize: 10);
        var expected = new PagedResult<AuditEvent>([], 0, 1, 10);
        _repo.QueryAsync(query, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.QueryAsync(query);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task CountAsync_DelegatesToRepository()
    {
        var query = new AuditQuery(Page: 1, PageSize: 10);
        _repo.CountAsync(query, Arg.Any<CancellationToken>()).Returns(42L);

        var result = await _sut.CountAsync(query);

        result.Should().Be(42);
    }

    [Fact]
    public async Task ExportAsync_Json_WritesToStream()
    {
        var query = new AuditQuery(Page: 1, PageSize: 50000);
        _repo.QueryAsync(Arg.Any<AuditQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<AuditEvent>([], 0, 1, 50000));

        using var stream = new MemoryStream();
        await _sut.ExportAsync(query, stream, ExportFormat.Json);

        stream.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExportAsync_Csv_WritesHeaderAndData()
    {
        var evt = CreateAuditEvent();
        var query = new AuditQuery(Page: 1, PageSize: 50000);
        _repo.QueryAsync(Arg.Any<AuditQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<AuditEvent>([evt], 1, 1, 50000));

        using var stream = new MemoryStream();
        await _sut.ExportAsync(query, stream, ExportFormat.Csv);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var csv = await reader.ReadToEndAsync();
        csv.Should().Contain("EventId,TimestampUtc,CorrelationId");
        csv.Should().Contain("VaultUnlocked");
    }

    private static AuditEvent CreateAuditEvent() => new(
        EventId: Guid.NewGuid(),
        TimestampUtc: DateTime.UtcNow,
        CorrelationId: "test-corr",
        Action: AuditAction.VaultUnlocked,
        ActorIdentity: "test-user",
        TargetMachineId: null,
        TargetMachineName: null,
        Detail: "Test event",
        Properties: null,
        Outcome: AuditOutcome.Success,
        ErrorMessage: null);
}
