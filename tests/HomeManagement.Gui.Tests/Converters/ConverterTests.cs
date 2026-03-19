using System.Globalization;
using FluentAssertions;
using HomeManagement.Gui.Converters;

namespace HomeManagement.Gui.Tests.Converters;

public sealed class TimeAgoConverterTests
{
    private readonly TimeAgoConverter _converter = TimeAgoConverter.Instance;

    [Fact]
    public void Convert_NonDateTime_ReturnsDash()
    {
        _converter.Convert("not a date", typeof(string), null, CultureInfo.InvariantCulture)
            .Should().Be("—");
    }

    [Fact]
    public void Convert_Null_ReturnsDash()
    {
        _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture)
            .Should().Be("—");
    }

    [Fact]
    public void Convert_JustNow_ReturnsJustNow()
    {
        var recent = DateTime.UtcNow.AddSeconds(-10);
        _converter.Convert(recent, typeof(string), null, CultureInfo.InvariantCulture)
            .Should().Be("just now");
    }

    [Fact]
    public void Convert_MinutesAgo_ReturnsMinuteFormat()
    {
        var time = DateTime.UtcNow.AddMinutes(-5);
        var result = (string)_converter.Convert(time, typeof(string), null, CultureInfo.InvariantCulture)!;
        result.Should().Contain("m ago");
    }

    [Fact]
    public void Convert_HoursAgo_ReturnsHourFormat()
    {
        var time = DateTime.UtcNow.AddHours(-3);
        var result = (string)_converter.Convert(time, typeof(string), null, CultureInfo.InvariantCulture)!;
        result.Should().Contain("h ago");
    }

    [Fact]
    public void Convert_Yesterday_ReturnsYesterday()
    {
        var time = DateTime.UtcNow.AddHours(-30);
        _converter.Convert(time, typeof(string), null, CultureInfo.InvariantCulture)
            .Should().Be("yesterday");
    }

    [Fact]
    public void Convert_DaysAgo_ReturnsDayFormat()
    {
        var time = DateTime.UtcNow.AddDays(-5);
        var result = (string)_converter.Convert(time, typeof(string), null, CultureInfo.InvariantCulture)!;
        result.Should().Contain("d ago");
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        var act = () => _converter.ConvertBack("test", typeof(DateTime), null, CultureInfo.InvariantCulture);
        act.Should().Throw<NotSupportedException>();
    }
}

public sealed class BoolToVisibilityConverterTests
{
    private readonly BoolToVisibilityConverter _converter = BoolToVisibilityConverter.Instance;

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Convert_NormalMode_ReturnsBoolValue(bool input, bool expected)
    {
        _converter.Convert(input, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Convert_InvertMode_ReturnsInvertedValue(bool input, bool expected)
    {
        _converter.Convert(input, typeof(bool), "invert", CultureInfo.InvariantCulture)
            .Should().Be(expected);
    }

    [Fact]
    public void Convert_InvertMode_CaseInsensitive()
    {
        _converter.Convert(true, typeof(bool), "INVERT", CultureInfo.InvariantCulture)
            .Should().Be(false);
    }

    [Fact]
    public void Convert_NullValue_ReturnsFalse()
    {
        _converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture)
            .Should().Be(false);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        var act = () => _converter.ConvertBack(true, typeof(bool), null, CultureInfo.InvariantCulture);
        act.Should().Throw<NotSupportedException>();
    }
}
