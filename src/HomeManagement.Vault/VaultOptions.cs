namespace HomeManagement.Vault;

/// <summary>
/// Configuration for the credential vault persistence layer.
/// </summary>
public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    /// <summary>
    /// Directory where <c>vault.dat</c> is stored.
    /// Defaults to <c>%APPDATA%/HomeManagement/vault</c> (or <c>~/.local/share/HomeManagement/vault</c> on Linux).
    /// </summary>
    public string StoragePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HomeManagement", "vault");
}
