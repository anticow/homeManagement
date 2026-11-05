using System.Text.Json;
using System.Text.Json.Serialization;
using HomeManagement.Integration.Prometheus.Models;

namespace HomeManagement.Integration.Prometheus;

/// <summary>Prometheus instant vector values use the wire format [unix_timestamp, "value"].</summary>
internal sealed class PrometheusValueJsonConverter : JsonConverter<PrometheusValue>
{
    public override PrometheusValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() != 2 || !root[0].TryGetDouble(out var timestamp) || root[1].ValueKind != JsonValueKind.String)
                throw new JsonException("Invalid Prometheus vector value.");
            return new(timestamp, root[1].GetString() ?? throw new JsonException("Missing Prometheus sample."));
        }

        // Retain compatibility with the previously supported object-shaped model representation.
        if (root.ValueKind == JsonValueKind.Object)
        {
            double? timestamp = null;
            string? value = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(nameof(PrometheusValue.Timestamp), StringComparison.OrdinalIgnoreCase))
                    timestamp = property.Value.GetDouble();
                else if (property.Name.Equals(nameof(PrometheusValue.ValueStr), StringComparison.OrdinalIgnoreCase))
                    value = property.Value.GetString();
            }
            if (timestamp.HasValue && value is not null) return new(timestamp.Value, value);
        }
        throw new JsonException("Invalid Prometheus vector value.");
    }

    public override void Write(Utf8JsonWriter writer, PrometheusValue value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Timestamp);
        writer.WriteStringValue(value.ValueStr);
        writer.WriteEndArray();
    }
}
