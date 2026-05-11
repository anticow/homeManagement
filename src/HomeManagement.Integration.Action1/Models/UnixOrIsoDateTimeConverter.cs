using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeManagement.Integration.Action1.Models;

/// <summary>
/// Handles Action1 API date fields that may be either:
///   - A Unix timestamp integer (seconds since epoch): 1715300000
///   - A Unix timestamp in milliseconds:               1715300000000
///   - An ISO 8601 string:                             "2026-05-10T20:00:00Z"
///   - null / absent
///
/// Action1 v3.0 returns Unix seconds for most date fields (last_seen, created_at, etc.).
/// </summary>
internal sealed class UnixOrIsoDateTimeConverter : JsonConverter<DateTime?>
{
    public static readonly UnixOrIsoDateTimeConverter Instance = new();

    // If the value is larger than this threshold it's likely milliseconds, not seconds.
    // (Jan 1 2100 in Unix seconds ≈ 4_102_444_800)
    private const long MillisecondsThreshold = 10_000_000_000L;

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                var unix = reader.GetInt64();
                // Determine if seconds or milliseconds
                return unix >= MillisecondsThreshold
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime
                    : DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

            case JsonTokenType.String:
                var raw = reader.GetString();
                if (string.IsNullOrEmpty(raw)) return null;

                // Try standard ISO 8601 / RFC 3339 first
                if (DateTime.TryParse(raw, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var iso))
                    return iso;

                // Action1 also uses a custom underscore-separated format: "2026-05-11_04-22-20"
                if (DateTime.TryParseExact(raw, "yyyy-MM-dd_HH-mm-ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var custom))
                    return custom;

                return null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value.ToString("O")); // ISO 8601 on output
    }
}

/// <summary>Non-nullable variant — falls back to DateTime.MinValue if null/missing.</summary>
internal sealed class UnixOrIsoDateTimeNonNullableConverter : JsonConverter<DateTime>
{
    public static readonly UnixOrIsoDateTimeNonNullableConverter Instance = new();

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => UnixOrIsoDateTimeConverter.Instance.Read(ref reader, typeof(DateTime?), options) ?? DateTime.MinValue;

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("O"));
}
