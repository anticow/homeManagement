using FluentAssertions;

namespace HomeManagement.Auditing.Tests;

public class SensitiveDataFilterTests
{
    private readonly SensitiveDataFilter _sut = new();

    // ── Redact free-text ──

    [Theory]
    [InlineData("password=secret123", "password= [REDACTED]")]
    [InlineData("token: abc-xyz", "token: [REDACTED]")]
    [InlineData("apikey=my-api-key-value", "apikey= [REDACTED]")]
    // The fixed regex stops at ';' so 'pwd=x' is redacted as a separate match — this is correct behavior.
    [InlineData("connectionstring=Server=db;pwd=x", "connectionstring= [REDACTED];pwd= [REDACTED]")]
    public void Redact_SensitivePatterns_AreRedacted(string input, string expected)
    {
        _sut.Redact(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("host=server1")]
    [InlineData("port=5432")]
    [InlineData("database=HomeManagement")]
    public void Redact_NonSensitiveText_IsPreserved(string input)
    {
        _sut.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_NullInput_ReturnsEmptyString()
    {
        _sut.Redact(null).Should().BeEmpty();
    }

    [Fact]
    public void Redact_EmptyInput_ReturnsEmptyString()
    {
        _sut.Redact(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Redact_MixedContent_OnlyRedactsSensitiveParts()
    {
        var input = "Connected to host=server1 with password=s3cret and port=5432";
        var result = _sut.Redact(input);

        result.Should().Contain("host=server1");
        result.Should().Contain("port=5432");
        result.Should().NotContain("s3cret");
    }

    // ── RedactProperties ──

    [Fact]
    public void RedactProperties_NullInput_ReturnsNull()
    {
        _sut.RedactProperties(null).Should().BeNull();
    }

    [Fact]
    public void RedactProperties_SensitiveKey_RedactsValue()
    {
        var props = new Dictionary<string, string>
        {
            ["password"] = "secret123",
            ["hostname"] = "server1"
        } as IReadOnlyDictionary<string, string>;

        var result = _sut.RedactProperties(props)!;

        result["password"].Should().Be("[REDACTED]");
        result["hostname"].Should().Be("server1");
    }

    [Fact]
    public void RedactProperties_KeyContainingSensitiveWord_RedactsValue()
    {
        var props = new Dictionary<string, string>
        {
            ["db_password_hash"] = "argon2id$...",
            ["api_token_expiry"] = "2025-01-01",
            ["client_secret_id"] = "abc-123",
            ["ssh_private_key_path"] = "/home/.ssh/id_rsa"
        } as IReadOnlyDictionary<string, string>;

        var result = _sut.RedactProperties(props)!;

        result["db_password_hash"].Should().Be("[REDACTED]");
        result["api_token_expiry"].Should().Be("[REDACTED]");
        result["client_secret_id"].Should().Be("[REDACTED]");
        result["ssh_private_key_path"].Should().Be("[REDACTED]");
    }

    [Fact]
    public void RedactProperties_NonSensitiveKey_WithSensitiveValuePattern_RedactsInValue()
    {
        var props = new Dictionary<string, string>
        {
            ["command"] = "export password=hunter2 && run"
        } as IReadOnlyDictionary<string, string>;

        var result = _sut.RedactProperties(props)!;

        result["command"].Should().NotContain("hunter2");
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("PASSWORD")]
    [InlineData("token")]
    [InlineData("apikey")]
    [InlineData("api_key")]
    [InlineData("private_key")]
    [InlineData("connectionstring")]
    [InlineData("bearer")]
    [InlineData("credential")]
    [InlineData("master_password")]
    public void RedactProperties_AllKnownSensitiveKeys_AreRedacted(string key)
    {
        var props = new Dictionary<string, string>
        {
            [key] = "some-value"
        } as IReadOnlyDictionary<string, string>;

        var result = _sut.RedactProperties(props)!;
        result[key].Should().Be("[REDACTED]");
    }
}
