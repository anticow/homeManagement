namespace HomeManagement.Abstractions.CrossCutting;

/// <summary>
/// Filters sensitive data (passwords, keys, tokens) from free-text fields before they reach
/// logs or the audit store. Applied automatically by the audit logger; can also be used by
/// any component that constructs human-readable messages.
/// </summary>
public interface ISensitiveDataFilter
{
    /// <summary>
    /// Redact any recognized sensitive patterns (passwords, keys, tokens) from <paramref name="input"/>.
    /// Returns the redacted string. If nothing sensitive is found, returns the original.
    /// </summary>
    string Redact(string? input);

    /// <summary>
    /// Redact values in a dictionary. Keys that match sensitive patterns have their values replaced.
    /// </summary>
    IReadOnlyDictionary<string, string>? RedactProperties(IReadOnlyDictionary<string, string>? properties);
}
