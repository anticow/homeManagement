namespace HomeManagement.Gui.Services;

/// <summary>
/// Provides cross-platform dialog primitives for the GUI.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows an informational message dialog.</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>Shows a confirmation dialog and returns true if the user confirms.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel");
}
