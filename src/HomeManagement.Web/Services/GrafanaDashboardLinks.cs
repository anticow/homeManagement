using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace HomeManagement.Web.Services;

public sealed record GrafanaPanelLink(string Title, string Url);

public sealed record GrafanaDashboardLinks(
    string Instance, string DashboardUrl, IReadOnlyList<GrafanaPanelLink> Panels)
{
    private static readonly string[] PanelTitles = ["CPU usage", "Memory usage", "Disk usage", "Network throughput"];

    public static GrafanaDashboardLinks Create(GrafanaOptions options, Machine machine, string from)
    {
        if (!options.IsConfigured || !options.IsValid)
            throw new ArgumentException("A valid Grafana configuration is required.", nameof(options));
        if (from is not ("now-1h" or "now-3h" or "now-24h" or "now-7d"))
            throw new ArgumentOutOfRangeException(nameof(from));

        var hostname = machine.Hostname.Value;
        var fqdn = string.IsNullOrWhiteSpace(machine.Fqdn) ? hostname : machine.Fqdn;
        var instance = machine.Tags.TryGetValue("prometheus:instance", out var label) && !string.IsNullOrWhiteSpace(label)
            ? label
            : options.InstanceLabelTemplate
                .Replace("{hostname}", hostname, StringComparison.OrdinalIgnoreCase)
                .Replace("{fqdn}", fqdn, StringComparison.OrdinalIgnoreCase)
                .Replace("{port}", machine.OsType == OsType.Windows ? "9182" : "9100", StringComparison.OrdinalIgnoreCase);
        var query = new Dictionary<string, string?>
        {
            ["orgId"] = "1",
            ["var-job"] = options.ScrapeJob,
            ["var-instance"] = instance,
            ["from"] = from,
            ["to"] = "now",
            ["refresh"] = "30s",
            ["theme"] = "light"
        };
        var root = options.BaseUrl.TrimEnd('/');
        var uid = Uri.EscapeDataString(options.DashboardUid);
        var dashboard = QueryHelpers.AddQueryString($"{root}/d/{uid}/node-metrics", query);
        var panelBase = QueryHelpers.AddQueryString($"{root}/d-solo/{uid}/node-metrics", query);
        var panels = PanelTitles
            .Select((title, index) => new GrafanaPanelLink(title,
                QueryHelpers.AddQueryString(panelBase, "panelId",
                    (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .ToArray();
        return new GrafanaDashboardLinks(instance, dashboard, panels);
    }
}
