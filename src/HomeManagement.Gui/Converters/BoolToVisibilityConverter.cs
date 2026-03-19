using System.Globalization;
using Avalonia.Data.Converters;

namespace HomeManagement.Gui.Converters;

/// <summary>
/// Converts a boolean to an IsVisible value. Pass parameter "invert" to reverse.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static BoolToVisibilityConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var boolValue = value is true;
        var invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
        return invert ? !boolValue : boolValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
