namespace HomeManagement.Gui.Services;

/// <summary>
/// Cross-platform clipboard access.
/// </summary>
public interface IClipboardService
{
    /// <summary>Copies text to the system clipboard.</summary>
    Task SetTextAsync(string text);

    /// <summary>Reads text from the system clipboard, or null if empty.</summary>
    Task<string?> GetTextAsync();
}
