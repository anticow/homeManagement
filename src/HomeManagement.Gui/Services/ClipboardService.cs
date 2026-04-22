using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace HomeManagement.Gui.Services;

/// <summary>
/// Clipboard service backed by the Avalonia platform clipboard.
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        var clipboard = GetClipboard();
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    public async Task<string?> GetTextAsync()
    {
        var clipboard = GetClipboard();
        if (clipboard is null)
            return null;
        return await clipboard.GetTextAsync();
    }

    private static IClipboard? GetClipboard()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow?.Clipboard;
        return null;
    }
}
