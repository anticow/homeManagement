namespace HomeManagement.Integration.Prometheus;

/// <summary>
/// Configuration for the Prometheus HTTP API integration.
/// Bind to "Prometheus" in appsettings.
/// </summary>
public sealed class PrometheusOptions
{
    public const string Section = "Prometheus";

    /// <summary>Prometheus HTTP API base URL, e.g. http://prometheus:9090</summary>
    public string Url { get; set; } = "http://localhost:9090";

    /// <summary>
    /// Label selector applied to all queries to scope results to homeManagement-managed endpoints.
    /// Expects job="{ScrapeLabel}" on all exported metrics.
    /// </summary>
    public string ScrapeLabel { get; set; } = "homemanagement";

    /// <summary>HTTP timeout for PromQL queries (seconds). Default 10.</summary>
    public int QueryTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// When false, a no-op provider is registered and Prometheus queries are skipped.
    /// Useful for environments where Prometheus is not yet deployed.
    /// </summary>
    public bool Enabled { get; set; }
}
