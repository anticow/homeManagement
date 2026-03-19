using System.Text.RegularExpressions;
using HomeManagement.Abstractions.CrossCutting;

namespace HomeManagement.Auditing;

/// <summary>
/// Filters sensitive data (passwords, keys, tokens) from free-text fields.
/// Applied by the audit logger before persisting events and usable by any logging component.
/// </summary>
internal sealed partial class SensitiveDataFilter : ISensitiveDataFilter
{
    // Pattern matches common secret formats: passwords, keys, tokens, connection strings
    [GeneratedRegex(
        @"(?i)(password|passwd|pwd|secret|token|apikey|api_key|private_key|connectionstring)\s*[:=]\s*\S+",
        RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SensitiveValuePattern();

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd", "secret", "token", "apikey", "api_key",
        "private_key", "privatekey", "connectionstring", "connection_string",
        "client_secret", "access_token", "refresh_token", "bearer",
        "credential", "passphrase", "master_password"
    };

    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        return SensitiveValuePattern().Replace(input, match =>
        {
            var eqIdx = match.Value.IndexOfAny([':', '=']);
            return eqIdx >= 0
                ? $"{match.Value[..(eqIdx + 1)]} [REDACTED]"
                : "[REDACTED]";
        });
    }

    public IReadOnlyDictionary<string, string>? RedactProperties(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null) return null;

        var redacted = new Dictionary<string, string>(properties.Count);
        foreach (var (key, value) in properties)
        {
            redacted[key] = IsSensitiveKey(key) ? "[REDACTED]" : Redact(value);
        }
        return redacted;
    }

    private static bool IsSensitiveKey(string key)
    {
        return SensitiveKeys.Contains(key)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("key", StringComparison.OrdinalIgnoreCase);
    }
}
