using Avalonia.Controls;
using Avalonia.Controls.Templates;
using HomeManagement.Gui.ViewModels;

namespace HomeManagement.Gui.Services;

/// <summary>
/// Maps ViewModel types to View types by naming convention:
/// FooViewModel → FooView (in the Views namespace).
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is null)
            return new TextBlock { Text = "No content" };

        var vmTypeName = data.GetType().FullName!;
        var viewTypeName = vmTypeName
            .Replace("ViewModels", "Views")
            .Replace("ViewModel", "View");

        var viewType = Type.GetType(viewTypeName);

        if (viewType is not null)
            return (Control)Activator.CreateInstance(viewType)!;

        return new TextBlock { Text = $"View not found: {viewTypeName}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
