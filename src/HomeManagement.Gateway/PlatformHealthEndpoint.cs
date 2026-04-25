using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace HomeManagement.Gateway;

/// <summary>
/// Exposes GET /platform-health — an unauthenticated, HTTP-accessible page that fans out
/// parallel health checks to every platform component and renders the result as HTML (for browsers)
/// or JSON (for programmatic consumers).
/// </summary>
internal static class PlatformHealthEndpoint
{
    // Config key → (display name, category)
    private static readonly (string ConfigKey, string Name, string Category)[] Services =
    [
        ("PlatformHealth:BrokerUrl",       "Broker",        "HomeManagement"),
        ("PlatformHealth:AuthUrl",         "Auth",          "HomeManagement"),
        ("PlatformHealth:WebUrl",          "Web",           "HomeManagement"),
        ("PlatformHealth:AgentGatewayUrl", "Agent Gateway", "HomeManagement"),
        ("PlatformHealth:SeqUrl",          "Seq",           "Platform"),
        ("PlatformHealth:PrometheusUrl",   "Prometheus",    "Platform"),
        ("PlatformHealth:GrafanaUrl",      "Grafana",       "Platform"),
        ("PlatformHealth:ArgoCDUrl",       "ArgoCD",        "Platform"),
        ("PlatformHealth:AwxUrl",          "AWX",           "Platform"),
    ];

    public static void Map(WebApplication app) =>
        app.MapGet("/platform-health", Handle).AllowAnonymous().ExcludeFromDescription();

    private static async Task<IResult> Handle(
        HttpContext ctx,
        IHttpClientFactory factory,
        IConfiguration config,
        CancellationToken ct)
    {
        var client = factory.CreateClient("platform-health");
        var now = DateTime.UtcNow;

        var tasks = Services.Select(async svc =>
        {
            var url = config[svc.ConfigKey];
            if (string.IsNullOrWhiteSpace(url))
                return new CheckResult(svc.Name, svc.Category, "Skipped", 0, "Not configured");

            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                var status = resp.IsSuccessStatusCode ? "Healthy" : "Unhealthy";
                return new CheckResult(svc.Name, svc.Category, status, (int)sw.ElapsedMilliseconds,
                    $"{(int)resp.StatusCode} {resp.ReasonPhrase}");
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Trim stack trace — just the top-level message is enough for the health page
                var msg = ex.Message.Split('\n')[0].Trim();
                return new CheckResult(svc.Name, svc.Category, "Unhealthy", (int)sw.ElapsedMilliseconds, msg);
            }
        });

        var results = await Task.WhenAll(tasks);

        var overall = results.Any(r => r.Status == "Unhealthy") ? "Unhealthy"
                    : results.Any(r => r.Status == "Degraded")  ? "Degraded"
                    : "Healthy";

        var wantsHtml = ctx.Request.Headers.Accept.ToString()
            .Contains("text/html", StringComparison.OrdinalIgnoreCase);

        return wantsHtml
            ? Results.Content(BuildHtml(results, overall, now), "text/html; charset=utf-8")
            : Results.Ok(new { status = overall, checkedAtUtc = now, components = results });
    }

    private static string BuildHtml(CheckResult[] results, string overall, DateTime now)
    {
        var bannerColor = overall switch
        {
            "Healthy"   => "#22c55e",
            "Degraded"  => "#f59e0b",
            _           => "#ef4444",
        };

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8"/>
              <meta http-equiv="refresh" content="30"/>
              <meta name="viewport" content="width=device-width,initial-scale=1"/>
              <title>Platform Health · cowgomu.net</title>
              <style>
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body { font-family: system-ui, sans-serif; background: #0f172a; color: #e2e8f0; padding: 2rem; max-width: 900px; margin: 0 auto; }
                h1 { font-size: 1.5rem; font-weight: 700; margin-bottom: .25rem; }
                .subtitle { color: #94a3b8; font-size: .875rem; margin-bottom: 1.5rem; }
                .banner { display: inline-flex; align-items: center; gap: .5rem; padding: .5rem 1.25rem; border-radius: .5rem;
                          background: {{bannerColor}}22; border: 1px solid {{bannerColor}}; color: {{bannerColor}};
                          font-weight: 700; font-size: 1.125rem; margin-bottom: 1.75rem; }
                .dot { width: .65rem; height: .65rem; border-radius: 50%; background: {{bannerColor}}; }
                table { width: 100%; border-collapse: collapse; background: #1e293b; border-radius: .75rem; overflow: hidden; }
                th { text-align: left; padding: .6rem 1rem; font-size: .7rem; text-transform: uppercase;
                     letter-spacing: .06em; color: #64748b; background: #0f172a; }
                td { padding: .65rem 1rem; border-top: 1px solid #334155; font-size: .85rem; }
                .pill { display: inline-block; padding: .2rem .6rem; border-radius: .25rem; font-size: .75rem; font-weight: 600; }
                .Healthy   { background: #14532d; color: #86efac; }
                .Unhealthy { background: #450a0a; color: #fca5a5; }
                .Degraded  { background: #431407; color: #fed7aa; }
                .Skipped   { background: #1e293b; color: #64748b; }
                .muted { color: #64748b; font-size: .8rem; }
                .footer { margin-top: 1rem; color: #334155; font-size: .75rem; text-align: right; }
              </style>
            </head>
            <body>
              <h1>Platform Health</h1>
              <p class="subtitle">Checked {{now:yyyy-MM-dd HH:mm:ss}} UTC &nbsp;·&nbsp; Auto-refreshes every 30 s</p>
              <div class="banner"><span class="dot"></span>{{overall}}</div>
              <table>
                <thead>
                  <tr>
                    <th>Component</th><th>Category</th><th>Status</th><th>Latency</th><th>Detail</th>
                  </tr>
                </thead>
                <tbody>
            """);

        foreach (var r in results)
        {
            var latency = r.LatencyMs > 0 ? $"{r.LatencyMs} ms" : "—";
            var detail = WebUtility.HtmlEncode(r.Detail ?? string.Empty);
            sb.Append(CultureInfo.InvariantCulture, $$"""

                      <tr>
                        <td>{{WebUtility.HtmlEncode(r.Name)}}</td>
                        <td class="muted">{{WebUtility.HtmlEncode(r.Category)}}</td>
                        <td><span class="pill {{r.Status}}">{{r.Status}}</span></td>
                        <td class="muted">{{latency}}</td>
                        <td class="muted">{{detail}}</td>
                      </tr>
                """);
        }

        sb.Append("""

                  </tbody>
                </table>
                <p class="footer">HomeManagement Platform · cowgomu.net</p>
              </body>
              </html>
            """);

        return sb.ToString();
    }

    private sealed record CheckResult(
        string Name,
        string Category,
        string Status,
        int LatencyMs,
        string? Detail);
}
