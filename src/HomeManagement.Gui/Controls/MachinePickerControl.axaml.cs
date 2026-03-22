using Avalonia;
using Avalonia.Controls;
using HomeManagement.Abstractions.Models;
using System.Collections;

namespace HomeManagement.Gui.Controls;

/// <summary>
/// Reusable machine picker with text filtering and multi-select support.
/// Bind <see cref="Machines"/> to the full list and <see cref="SelectedMachines"/> to receive selections.
/// </summary>
public partial class MachinePickerControl : UserControl
{
    public static readonly StyledProperty<IEnumerable?> MachinesProperty =
        AvaloniaProperty.Register<MachinePickerControl, IEnumerable?>(nameof(Machines));

    public static readonly StyledProperty<IList?> SelectedMachinesProperty =
        AvaloniaProperty.Register<MachinePickerControl, IList?>(nameof(SelectedMachines));

    public static readonly StyledProperty<string?> FilterTextProperty =
        AvaloniaProperty.Register<MachinePickerControl, string?>(nameof(FilterText));

    public IEnumerable? Machines
    {
        get => GetValue(MachinesProperty);
        set => SetValue(MachinesProperty, value);
    }

    public IList? SelectedMachines
    {
        get => GetValue(SelectedMachinesProperty);
        set => SetValue(SelectedMachinesProperty, value);
    }

    public string? FilterText
    {
        get => GetValue(FilterTextProperty);
        set => SetValue(FilterTextProperty, value);
    }

    public MachinePickerControl()
    {
        InitializeComponent();
    }
}
