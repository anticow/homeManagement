namespace HomeManagement.Web.Services;

/// <summary>
/// Configuration for embedding Grafana panels inside the HomeManagement UX.
/// Set BaseUrl + dashboard UIDs to enable the Grafana section in MachineDetail.
/// </summary>
public sealed class GrafanaOptions
{
    public const string SectionName = "Grafana";

    /// <summary>Public base URL for Grafana, e.g. https://grafana.cowgomu.net</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Grafana dashboard UID for Node Exporter Full (used for Linux and macOS hosts).
    /// Find it in the Grafana URL after importing the Node Exporter Full dashboard:
    ///   https://grafana.cowgomu.net/d/{uid}/node-exporter-full
    /// </summary>
    public string NodeExporterDashboardUid { get; set; } = string.Empty;

    /// <summary>
    /// Grafana dashboard UID for Windows Exporter (used for Windows hosts).
    /// Find it after importing a Windows Exporter dashboard:
    ///   https://grafana.cowgomu.net/d/{uid}/windows-exporter
    /// </summary>
    public string WindowsExporterDashboardUid { get; set; } = string.Empty;

    /// <summary>
    /// Grafana panel ID for CPU usage within the node exporter dashboard.
    /// Defaults to panel 3 (Node Exporter Full community dashboard 1860).
    /// </summary>
    public int CpuPanelId { get; set; } = 3;

    /// <summary>
    /// Grafana panel ID for memory usage within the node exporter dashboard.
    /// Defaults to panel 11 (Node Exporter Full community dashboard 1860).
    /// </summary>
    public int MemoryPanelId { get; set; } = 11;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        (!string.IsNullOrWhiteSpace(NodeExporterDashboardUid) || !string.IsNullOrWhiteSpace(WindowsExporterDashboardUid));
}
