using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HomeManagement.Abstractions;

namespace HomeManagement.Gui.Converters;

/// <summary>
/// Converts MachineState, ServiceState, or JobState enum values to colored brushes.
/// </summary>
public sealed class StateToColorConverter : IValueConverter
{
    public static StateToColorConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            MachineState.Online => Brushes.LimeGreen,
            MachineState.Offline => Brushes.Red,
            MachineState.Unreachable => Brushes.Orange,
            MachineState.Maintenance => Brushes.DodgerBlue,

            ServiceState.Running => Brushes.LimeGreen,
            ServiceState.Stopped => Brushes.Gray,
            ServiceState.Starting or ServiceState.Stopping => Brushes.Orange,
            ServiceState.Paused => Brushes.DodgerBlue,

            JobState.Running => Brushes.DodgerBlue,
            JobState.Completed => Brushes.LimeGreen,
            JobState.Failed => Brushes.Red,
            JobState.Cancelled => Brushes.Gray,
            JobState.Queued => Brushes.Orange,

            _ => Brushes.Gray
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
