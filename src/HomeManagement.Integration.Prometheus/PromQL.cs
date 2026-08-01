namespace HomeManagement.Integration.Prometheus;

/// <summary>
/// Static helpers for building PromQL query strings targeting homeManagement-managed endpoints.
/// All queries expect node_exporter (Linux) or windows_exporter (Windows) metrics,
/// scoped with a job label matching <see cref="PrometheusOptions.ScrapeLabel"/>.
/// </summary>
public static class PromQL
{
    // ── Service state ─────────────────────────────────────────────────────────

    /// <summary>
    /// Windows service state via windows_exporter.
    /// Returns 1 for the matching state, 0 otherwise.
    /// State values: "running" | "stopped" | "start_pending" | "stop_pending" | "paused"
    /// </summary>
    public static string WindowsServiceState(string hostname, string serviceName, string scrapeLabel) =>
        $$"""windows_service_state{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",name="{{EscapeLabel(serviceName)}}"}""";

    /// <summary>
    /// Linux systemd unit state via node_exporter.
    /// Returns 1 for the active state, 0 for others.
    /// State values: "active" | "activating" | "deactivating" | "inactive" | "failed" | "unknown"
    /// </summary>
    public static string LinuxSystemdUnitState(string hostname, string unitName, string scrapeLabel) =>
        $$"""node_systemd_unit_state{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",name="{{EscapeLabel(unitName)}}"}""";

    // ── Availability ──────────────────────────────────────────────────────────

    /// <summary>
    /// Prometheus 'up' metric — 1 if the scrape target is reachable, 0 if not.
    /// </summary>
    public static string EndpointUp(string hostname, string scrapeLabel) =>
        $$"""up{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"}""";

    // ── Memory ────────────────────────────────────────────────────────────────

    /// <summary>Available memory bytes (Linux node_exporter).</summary>
    public static string LinuxMemoryAvailableBytes(string hostname, string scrapeLabel) =>
        $$"""node_memory_MemAvailable_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"}""";

    /// <summary>Total memory bytes (Linux node_exporter).</summary>
    public static string LinuxMemoryTotalBytes(string hostname, string scrapeLabel) =>
        $$"""node_memory_MemTotal_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"}""";

    /// <summary>Available memory bytes (Windows windows_exporter).</summary>
    public static string WindowsMemoryAvailableBytes(string hostname, string scrapeLabel) =>
        $$"""windows_memory_available_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"}""";

    /// <summary>Total physical memory bytes (Windows windows_exporter).</summary>
    public static string WindowsMemoryPhysicalBytes(string hostname, string scrapeLabel) =>
        $$"""windows_cs_physical_memory_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"}""";

    /// <summary>Memory usage percent (Linux node_exporter). 0–100.</summary>
    public static string LinuxMemoryUsagePercent(string hostname, string scrapeLabel) =>
        $$"""(1 - (node_memory_MemAvailable_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"} / node_memory_MemTotal_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"})) * 100""";

    /// <summary>Memory usage percent (Windows windows_exporter). 0–100.</summary>
    public static string WindowsMemoryUsagePercent(string hostname, string scrapeLabel) =>
        $$"""(1 - (windows_memory_available_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"} / windows_cs_physical_memory_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"})) * 100""";

    /// <summary>Memory usage percent (macOS node_exporter — same as Linux).</summary>
    public static string MacOsMemoryUsagePercent(string hostname, string scrapeLabel) =>
        LinuxMemoryUsagePercent(hostname, scrapeLabel);

    // ── Disk ──────────────────────────────────────────────────────────────────

    /// <summary>Disk free bytes on the root mount (Linux node_exporter).</summary>
    public static string LinuxDiskFreeBytes(string hostname, string scrapeLabel) =>
        $$"""node_filesystem_avail_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",mountpoint="/",fstype!="tmpfs"}""";

    /// <summary>Disk total bytes on the root mount (Linux node_exporter).</summary>
    public static string LinuxDiskTotalBytes(string hostname, string scrapeLabel) =>
        $$"""node_filesystem_size_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",mountpoint="/",fstype!="tmpfs"}""";

    /// <summary>Disk free bytes on C:\ (Windows windows_exporter).</summary>
    public static string WindowsDiskFreeBytes(string hostname, string scrapeLabel) =>
        $$"""windows_logical_disk_free_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",volume="C:"}""";

    /// <summary>Disk total bytes on C:\ (Windows windows_exporter).</summary>
    public static string WindowsDiskSizeBytes(string hostname, string scrapeLabel) =>
        $$"""windows_logical_disk_size_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",volume="C:"}""";

    /// <summary>Disk usage percent on root mount (Linux node_exporter). 0–100.</summary>
    public static string LinuxDiskUsagePercent(string hostname, string scrapeLabel) =>
        $$"""(1 - (node_filesystem_avail_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",mountpoint="/",fstype!="tmpfs"} / node_filesystem_size_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",mountpoint="/",fstype!="tmpfs"})) * 100""";

    /// <summary>Disk usage percent on C:\ (Windows windows_exporter). 0–100.</summary>
    public static string WindowsDiskUsagePercent(string hostname, string scrapeLabel) =>
        $$"""(1 - (windows_logical_disk_free_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",volume="C:"} / windows_logical_disk_size_bytes{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",volume="C:"})) * 100""";

    /// <summary>Disk usage percent on root mount (macOS node_exporter — same as Linux).</summary>
    public static string MacOsDiskUsagePercent(string hostname, string scrapeLabel) =>
        LinuxDiskUsagePercent(hostname, scrapeLabel);

    // ── CPU ───────────────────────────────────────────────────────────────────

    /// <summary>CPU usage percent averaged over 5 minutes (Linux node_exporter).</summary>
    public static string LinuxCpuUsagePercent(string hostname, string scrapeLabel) =>
        $$"""100 - (avg by (instance) (rate(node_cpu_seconds_total{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",mode="idle"}[5m])) * 100)""";

    /// <summary>CPU usage percent averaged over 5 minutes (Windows windows_exporter).</summary>
    public static string WindowsCpuUsagePercent(string hostname, string scrapeLabel) =>
        $$"""100 - (avg by (instance) (rate(windows_cpu_time_total{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*",mode="idle"}[5m])) * 100)""";

    /// <summary>CPU usage percent averaged over 5 minutes (macOS node_exporter — same as Linux).</summary>
    public static string MacOsCpuUsagePercent(string hostname, string scrapeLabel) =>
        LinuxCpuUsagePercent(hostname, scrapeLabel);

    // ── Uptime ────────────────────────────────────────────────────────────────

    /// <summary>System boot time (seconds since epoch) — Linux node_exporter.</summary>
    public static string LinuxBootTimeSeconds(string hostname, string scrapeLabel) =>
        $$"""node_boot_time_seconds{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"}""";

    /// <summary>System uptime seconds — Windows windows_exporter.</summary>
    public static string WindowsSystemUptimeSeconds(string hostname, string scrapeLabel) =>
        $$"""windows_system_system_up_time{job="{{scrapeLabel}}",instance=~"{{EscapeLabel(hostname)}}.*"}""";

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Escape a value for use inside a PromQL label matcher (escapes \ and ").</summary>
    private static string EscapeLabel(string value) =>
        value.Replace(@"\", @"\\").Replace("\"", "\\\"");
}
