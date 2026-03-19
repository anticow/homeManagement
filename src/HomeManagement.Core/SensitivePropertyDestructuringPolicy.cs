using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace HomeManagement.Core;

/// <summary>
/// Serilog enricher that redacts log event property values whose keys match
/// known sensitive patterns (password, secret, token, key, etc.).
/// Wired into the Serilog pipeline during <see cref="LoggingBootstrap"/> configuration.
/// </summary>
internal sealed partial class SensitivePropertyEnricher : ILogEventEnricher
{
    [GeneratedRegex(
        @"password|passwd|pwd|secret|token|apikey|api_key|private_key|connectionstring|credential|passphrase|bearer",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex SensitiveKeyPattern();

    private static readonly ScalarValue RedactedValue = new("[REDACTED]");

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        List<string>? keysToRedact = null;

        foreach (var kvp in logEvent.Properties)
        {
            if (kvp.Value is ScalarValue sv && sv.Value is string && SensitiveKeyPattern().IsMatch(kvp.Key))
            {
                keysToRedact ??= [];
                keysToRedact.Add(kvp.Key);
            }
        }

        if (keysToRedact is null) return;

        foreach (var key in keysToRedact)
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(key, RedactedValue));
        }
    }
}
