using FluentAssertions;
using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Abstractions.Tests.Validation;

public sealed class HostnameTests
{
    [Theory]
    [InlineData("server1")]
    [InlineData("my-host.local")]
    [InlineData("web01.prod.example.com")]
    [InlineData("a")]
    [InlineData("10.0.0.1")]
    public void TryCreate_ValidHostname_ReturnsTrue(string value)
    {
        Hostname.TryCreate(value, out var result, out var error).Should().BeTrue();
        result.Value.Should().Be(value);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("", "cannot be empty")]
    [InlineData("   ", "cannot be empty")]
    [InlineData("-starts-with-hyphen", "invalid characters")]
    [InlineData("ends-with-hyphen-", "invalid characters")]
    [InlineData("has space", "invalid characters")]
    [InlineData("has!bang", "invalid characters")]
    public void TryCreate_InvalidHostname_ReturnsFalse(string value, string errorContains)
    {
        Hostname.TryCreate(value, out _, out var error).Should().BeFalse();
        error.Should().Contain(errorContains, "validation should explain the failure");
    }

    [Fact]
    public void TryCreate_ExceedsMaxLength_ReturnsFalse()
    {
        var tooLong = new string('a', 254);
        Hostname.TryCreate(tooLong, out _, out var error).Should().BeFalse();
        error.Should().Contain("253");
    }

    [Fact]
    public void Create_InvalidValue_ThrowsArgumentException()
    {
        var act = () => Hostname.Create("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Equals_CaseInsensitive()
    {
        var a = Hostname.Create("Server1");
        var b = Hostname.Create("server1");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        Hostname.Create("myhost").ToString().Should().Be("myhost");
    }
}

public sealed class ServiceNameTests
{
    [Theory]
    [InlineData("sshd")]
    [InlineData("nginx")]
    [InlineData("my-service_v2")]
    [InlineData("user@.service")]
    public void TryCreate_ValidServiceName_ReturnsTrue(string value)
    {
        ServiceName.TryCreate(value, out var result, out var error).Should().BeTrue();
        result.Value.Should().Be(value);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("", "cannot be empty")]
    [InlineData("   ", "cannot be empty")]
    [InlineData("-starts-bad", "invalid characters")]
    [InlineData("has space", "invalid characters")]
    [InlineData("rm -rf /", "invalid characters")]
    [InlineData("; drop table", "invalid characters")]
    public void TryCreate_InvalidServiceName_ReturnsFalse(string value, string errorContains)
    {
        ServiceName.TryCreate(value, out _, out var error).Should().BeFalse();
        error.Should().Contain(errorContains);
    }

    [Fact]
    public void Equals_CaseInsensitive()
    {
        var a = ServiceName.Create("Nginx");
        var b = ServiceName.Create("nginx");
        a.Should().Be(b);
    }
}

public sealed class CidrRangeTests
{
    [Theory]
    [InlineData("192.168.1.0/24")]
    [InlineData("10.0.0.0/8")]
    [InlineData("172.16.0.0/12")]
    [InlineData("0.0.0.0/0")]
    public void TryCreate_ValidCidr_ReturnsTrue(string value)
    {
        CidrRange.TryCreate(value, out var result, out var error).Should().BeTrue();
        result.Value.Should().Be(value);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("", "cannot be empty")]
    [InlineData("not-a-cidr", "Invalid CIDR")]
    [InlineData("192.168.1.0", "Invalid CIDR")]
    [InlineData("999.999.999.999/24", "invalid octets")]
    [InlineData("192.168.1.0/33", "invalid octets")]
    public void TryCreate_InvalidCidr_ReturnsFalse(string value, string errorContains)
    {
        CidrRange.TryCreate(value, out _, out var error).Should().BeFalse();
        error.Should().Contain(errorContains);
    }

    [Fact]
    public void Equals_CaseSensitive()
    {
        var a = CidrRange.Create("192.168.1.0/24");
        var b = CidrRange.Create("192.168.1.0/24");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void NotEquals_DifferentRanges()
    {
        var a = CidrRange.Create("192.168.1.0/24");
        var b = CidrRange.Create("10.0.0.0/8");
        (a != b).Should().BeTrue();
    }
}
