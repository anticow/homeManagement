using System.Globalization;
using Avalonia.Data.Converters;

namespace HomeManagement.Gui.Converters;

/// <summary>
/// Converts a DateTime to a human-readable relative time string.
/// </summary>
public sealed class TimeAgoConverter : IValueConverter
{
    public static TimeAgoConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime dateTime)
            return "—";

        // Normalize to UTC to prevent incorrect elapsed calculations (NEW-07)
        if (dateTime.Kind != DateTimeKind.Utc)
            dateTime = dateTime.ToUniversalTime();

        var elapsed = DateTime.UtcNow - dateTime;

        return elapsed.TotalSeconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)elapsed.TotalMinutes}m ago",
            < 86400 => $"{(int)elapsed.TotalHours}h ago",
            < 172800 => "yesterday",
            _ => $"{(int)elapsed.TotalDays}d ago"
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
