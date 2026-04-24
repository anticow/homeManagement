namespace HomeManagement.Integration.Prometheus.Models;

// ── Prometheus HTTP API Response Models ───────────────────────────────────────
// Models the Prometheus /api/v1/query and /api/v1/query_range responses.
// https://prometheus.io/docs/prometheus/latest/querying/api/

public sealed record PrometheusApiResponse<T>(
    string Status,   // "success" | "error"
    T? Data,
    string? ErrorType,
    string? Error);

public sealed record PrometheusQueryData(
    string ResultType,  // "vector" | "matrix" | "scalar" | "string"
    IReadOnlyList<PrometheusVector> Result);

/// <summary>A single instant-vector result from a Prometheus query.</summary>
public sealed record PrometheusVector(
    IReadOnlyDictionary<string, string> Metric,
    PrometheusValue Value);

/// <summary>[unix_timestamp, "value_string"] pair from Prometheus.</summary>
public sealed record PrometheusValue(double Timestamp, string ValueStr)
{
    /// <summary>Parse ValueStr as double. Returns null if not parseable.</summary>
    public double? AsDouble() =>
        double.TryParse(ValueStr, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}

// ── Derived domain models ─────────────────────────────────────────────────────

/// <summary>Resolved state of a system service on a managed endpoint.</summary>
public sealed record EndpointServiceState(
    string Hostname,
    string ServiceName,
    string State,         // "running" | "stopped" | "unknown"
    string? StartupType); // "auto" | "manual" | "disabled" | null

/// <summary>Snapshot of system resource metrics for an endpoint.</summary>
public sealed record EndpointMetrics(
    string Hostname,
    double? CpuUsagePercent,
    double? MemoryUsedBytes,
    double? MemoryTotalBytes,
    double? DiskFreeBytes,
    double? DiskTotalBytes,
    TimeSpan? Uptime,
    DateTime SampledAtUtc);

/// <summary>Online/offline status of an endpoint from Prometheus 'up' metric.</summary>
public sealed record EndpointAvailability(
    string Hostname,
    bool IsOnline,
    DateTime SampledAtUtc);
