using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace HomeManagement.Gui.Services;

/// <summary>
/// Avalonia-based dialog service using child windows as modal dialogs.
/// </summary>
public sealed class DialogService : IDialogService
{
    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = CreateDialog(title, message, showCancel: false);
        var owner = GetOwnerWindow();
        if (owner is not null)
            await dialog.ShowDialog(owner);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel")
    {
        bool confirmed = false;

        var confirmButton = new Button { Content = confirmText, Width = 100, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var cancelButton = new Button { Content = cancelText, Width = 100, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(24),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { confirmButton, cancelButton }
                    }
                }
            }
        };

        confirmButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancelButton.Click += (_, _) => { confirmed = false; dialog.Close(); };

        var owner = GetOwnerWindow();
        if (owner is not null)
            await dialog.ShowDialog(owner);

        return confirmed;
    }

    private static Window CreateDialog(string title, string message, bool showCancel)
    {
        var okButton = new Button { Content = "OK", Width = 80, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { okButton }
        };

        if (showCancel)
        {
            buttonPanel.Children.Add(new Button { Content = "Cancel", Width = 80 });
        }

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 16,
                Margin = new Avalonia.Thickness(24),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    buttonPanel
                }
            }
        };

        okButton.Click += (_, _) => dialog.Close();

        return dialog;
    }

    private static Window? GetOwnerWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }
}
