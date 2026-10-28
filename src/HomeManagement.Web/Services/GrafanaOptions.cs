namespace HomeManagement.Web.Services;

public sealed class GrafanaOptions
{
    public const string SectionName = "Grafana";

    public string BaseUrl { get; set; } = string.Empty;
    public string DashboardUid { get; set; } = "hm-node";
    public string ScrapeJob { get; set; } = "homemanagement";

    /// <summary>
    /// Must match Prometheus's exact instance label. A machine's prometheus:instance
    /// tag overrides this template. Supported placeholders: {hostname}, {fqdn}, {port}.
    /// </summary>
    public string InstanceLabelTemplate { get; set; } = "{fqdn}:{port}";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    public bool IsValid =>
        !IsConfigured ||
        (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
         (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
         string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
         string.IsNullOrEmpty(uri.Fragment) &&
         !string.IsNullOrWhiteSpace(DashboardUid) &&
         !string.IsNullOrWhiteSpace(ScrapeJob) &&
         !string.IsNullOrWhiteSpace(InstanceLabelTemplate));
}
