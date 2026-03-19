using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;

namespace HomeManagement.Core.Tests;

/// <summary>
/// Tests for <see cref="SensitivePropertyEnricher"/> — verifies MED-06 sensitive data redaction.
/// </summary>
public sealed class SensitivePropertyEnricherTests
{
    private static readonly MessageTemplate EmptyTemplate =
        new MessageTemplateParser().Parse("Test message");

    private static LogEvent CreateEvent(params (string Key, string Value)[] properties)
    {
        var props = new List<LogEventProperty>();
        foreach (var (key, value) in properties)
            props.Add(new LogEventProperty(key, new ScalarValue(value)));
        return new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, EmptyTemplate, props);
    }

    private readonly SensitivePropertyEnricher _enricher = new();

    // ── Redaction of known sensitive keys ──

    [Theory]
    [InlineData("Password")]
    [InlineData("password")]
    [InlineData("ConnectionString")]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("Secret")]
    [InlineData("Token")]
    [InlineData("private_key")]
    [InlineData("Credential")]
    [InlineData("Passphrase")]
    [InlineData("Bearer")]
    [InlineData("UserPassword")]
    [InlineData("db_passwd")]
    public void Enrich_SensitiveKey_RedactsValue(string key)
    {
        var logEvent = CreateEvent((key, "supersecret123"));
        _enricher.Enrich(logEvent, null!);

        logEvent.Properties[key].ToString().Should().Contain("REDACTED");
    }

    // ── Non-sensitive keys untouched ──

    [Theory]
    [InlineData("Username")]
    [InlineData("HostName")]
    [InlineData("RequestId")]
    [InlineData("Duration")]
    [InlineData("Endpoint")]
    public void Enrich_NonSensitiveKey_LeavesValueIntact(string key)
    {
        var logEvent = CreateEvent((key, "normalvalue"));
        _enricher.Enrich(logEvent, null!);

        var scalar = logEvent.Properties[key] as ScalarValue;
        scalar!.Value.Should().Be("normalvalue");
    }

    // ── Mixed properties ──

    [Fact]
    public void Enrich_MixedProperties_OnlyRedactsSensitive()
    {
        var logEvent = CreateEvent(
            ("Username", "admin"),
            ("Password", "secret"),
            ("Token", "abc123"),
            ("HostName", "server1"));

        _enricher.Enrich(logEvent, null!);

        // Password and Token should be redacted
        logEvent.Properties["Password"].ToString().Should().Contain("REDACTED");
        logEvent.Properties["Token"].ToString().Should().Contain("REDACTED");
        // Username and HostName should be intact
        (logEvent.Properties["Username"] as ScalarValue)!.Value.Should().Be("admin");
        (logEvent.Properties["HostName"] as ScalarValue)!.Value.Should().Be("server1");
    }

    // ── Non-string values are not redacted ──

    [Fact]
    public void Enrich_NonStringScalar_DoesNotRedact()
    {
        var props = new List<LogEventProperty>
        {
            new("Password", new ScalarValue(12345)) // int, not string
        };
        var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, EmptyTemplate, props);

        _enricher.Enrich(logEvent, null!);

        var scalar = logEvent.Properties["Password"] as ScalarValue;
        scalar!.Value.Should().Be(12345); // not redacted because it's not a string
    }

    // ── No properties ──

    [Fact]
    public void Enrich_NoProperties_DoesNotThrow()
    {
        var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, EmptyTemplate, []);
        _enricher.Invoking(e => e.Enrich(logEvent, null!)).Should().NotThrow();
    }
}
