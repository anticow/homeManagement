using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HomeManagement.Gateway;

/// <summary>
/// Exposes GET /platform-health — an unauthenticated, HTTP-accessible page that fans out
/// parallel health checks to every platform component and renders the result as HTML (for browsers)
/// or JSON (for programmatic consumers). Results are cached for 15 seconds to limit internal load.
/// </summary>
internal static class PlatformHealthEndpoint
{
    // ── Version source strategies ──────────────────────────────────────────────────────────────
    // Each strategy is best-effort: failure never changes health status, only shows "—" in UI.

    private abstract record VersionSource
    {
        /// <summary>Probe the service's own /version endpoint (path replaced on health URL).</summary>
        public sealed record HmEndpoint : VersionSource;
        /// <summary>Extract a JSON field from the health response body (no extra HTTP call).</summary>
        public sealed record ParseHealthBody(string Field) : VersionSource;
        /// <summary>Make a secondary GET to a derived path and extract a JSON field.</summary>
        public sealed record SecondaryCall(string Path, string Field) : VersionSource;
        /// <summary>No version information available for this service.</summary>
        public sealed record None : VersionSource;
    }

    private sealed record ServiceDef(
        string ConfigKey, string Name, string Category, VersionSource Version,
        string? PodLabel = null);

    private static readonly ServiceDef[] Services =
    [
        new("PlatformHealth:BrokerUrl",       "Broker",        "HomeManagement", new VersionSource.HmEndpoint(),  "hm-broker"),
        new("PlatformHealth:AuthUrl",         "Auth",          "HomeManagement", new VersionSource.HmEndpoint(),  "hm-auth"),
        new("PlatformHealth:WebUrl",          "Web",           "HomeManagement", new VersionSource.HmEndpoint(),  "hm-web"),
        new("PlatformHealth:AgentGatewayUrl", "Agent Gateway", "HomeManagement", new VersionSource.HmEndpoint(),  "hm-agent-gw"),
        new("PlatformHealth:SeqUrl",          "Seq",           "Platform",       new VersionSource.None()),
        new("PlatformHealth:PrometheusUrl",   "Prometheus",    "Platform",       new VersionSource.SecondaryCall("/api/v1/status/buildinfo", "data.version")),
        new("PlatformHealth:GrafanaUrl",      "Grafana",       "Platform",       new VersionSource.ParseHealthBody("version")),
        new("PlatformHealth:ArgoCDUrl",       "ArgoCD",        "Platform",       new VersionSource.SecondaryCall("/api/version", "Version")),
        new("PlatformHealth:AwxUrl",          "AWX",           "Platform",       new VersionSource.ParseHealthBody("version")),
    ];

    // ── 15-second result cache ─────────────────────────────────────────────────────────────────
    private static volatile CachedSnapshot? _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);
    private sealed record CachedSnapshot(DateTime FetchedAt, string Overall, CheckResult[] Results);

    // ── Gateway pod's own start time (process start, never changes while running) ───────────────
    private static readonly DateTime GatewayStartTime =
        Process.GetCurrentProcess().StartTime.ToUniversalTime();

    // ── Kubernetes in-cluster pod age lookup ──────────────────────────────────────────────────
    // Best-effort: any failure silently returns empty. Requires RBAC Role + RoleBinding granting
    // get/list on pods in the homemanagement namespace (see rbac.yaml Helm template).
    private static readonly string K8sTokenPath =
        "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private static readonly string K8sNsPath =
        "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    private static async Task<IReadOnlyDictionary<string, DateTime>> FetchPodStartTimesAsync(
        HttpClient k8sClient, CancellationToken ct)
    {
        if (!File.Exists(K8sTokenPath)) return new Dictionary<string, DateTime>();
        try
        {
            var token = await File.ReadAllTextAsync(K8sTokenPath, ct);
            var ns = File.Exists(K8sNsPath)
                ? (await File.ReadAllTextAsync(K8sNsPath, ct)).Trim()
                : "homemanagement";

            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://kubernetes.default.svc/api/v1/namespaces/{ns}/pods");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await k8sClient.SendAsync(req,
                HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode) return new Dictionary<string, DateTime>();

            var body = await resp.Content.ReadAsStringAsync(ct);
            return ParsePodStartTimes(body);
        }
        catch { return new Dictionary<string, DateTime>(); }
    }

    private static Dictionary<string, DateTime> ParsePodStartTimes(string json)
    {
        var result = new Dictionary<string, DateTime>();
        try
        {
            // Build reverse lookup: pod app label → service name
            var labelToService = Services
                .Where(s => s.PodLabel is not null)
                .ToDictionary(s => s.PodLabel!, s => s.Name);

            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (!item.TryGetProperty("metadata", out var meta)) continue;
                if (!meta.TryGetProperty("labels", out var labels)) continue;
                if (!labels.TryGetProperty("app", out var appEl)) continue;
                var appLabel = appEl.GetString();
                if (appLabel is null || !labelToService.TryGetValue(appLabel, out var svcName)) continue;

                if (!item.TryGetProperty("status", out var status)) continue;
                if (!status.TryGetProperty("startTime", out var startTimeEl)) continue;
                if (!DateTime.TryParse(startTimeEl.GetString(), null,
                        DateTimeStyles.RoundtripKind, out var t)) continue;

                // Keep the earliest start time when multiple replicas exist
                if (!result.TryGetValue(svcName, out var existing) || t < existing)
                    result[svcName] = t;
            }
        }
        catch { /* best effort */ }
        return result;
    }

    private static string FormatAge(TimeSpan age) => age.TotalDays >= 1
        ? $"{(int)age.TotalDays}d {age.Hours}h"
        : age.TotalHours >= 1
            ? $"{(int)age.TotalHours}h {age.Minutes}m"
            : $"{(int)age.TotalMinutes}m";

    // ── Endpoint registration ──────────────────────────────────────────────────────────────────

    public static void Map(WebApplication app) =>
        app.MapGet("/platform-health", Handle).AllowAnonymous().ExcludeFromDescription();

    private static async Task<IResult> Handle(
        HttpContext ctx,
        IHttpClientFactory factory,
        IConfiguration config,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cached = _cache;
        if (cached is not null && now - cached.FetchedAt < CacheDuration)
            return Respond(ctx, cached.Results, cached.Overall, cached.FetchedAt);

        var healthClient = factory.CreateClient("platform-health");
        var k8sClient = factory.CreateClient("k8s-api");

        // Fan out health checks and pod start-time lookup concurrently
        var healthTasks = Services.Select(svc => CheckServiceAsync(healthClient, config, svc, ct)).ToArray();
        var podTimesTask = FetchPodStartTimesAsync(k8sClient, ct);

        await Task.WhenAll(healthTasks.Cast<Task>().Append(podTimesTask));

        var results = await Task.WhenAll(healthTasks);
        var podTimes = await podTimesTask;

        // Enrich results with pod start times where available
        var enriched = results
            .Select(r => podTimes.TryGetValue(r.Name, out var t) ? r with { PodStartTime = t } : r)
            .ToArray();

        var overall = enriched.Any(r => r.Status == "Unhealthy") ? "Unhealthy"
                    : enriched.Any(r => r.Status == "Degraded") ? "Degraded"
                    : "Healthy";

        _cache = new CachedSnapshot(now, overall, enriched);
        return Respond(ctx, enriched, overall, now);
    }

    private static IResult Respond(HttpContext ctx, CheckResult[] results, string overall, DateTime checkedAt)
    {
        var gatewayVersion = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";
        var wantsHtml = ctx.Request.Headers.Accept.ToString()
            .Contains("text/html", StringComparison.OrdinalIgnoreCase);
        return wantsHtml
            ? Results.Content(BuildHtml(results, overall, checkedAt, gatewayVersion), "text/html; charset=utf-8")
            : Results.Ok(new { status = overall, version = gatewayVersion, checkedAtUtc = checkedAt, components = results });
    }

    // ── Health + version probing ───────────────────────────────────────────────────────────────

    private static async Task<CheckResult> CheckServiceAsync(
        HttpClient client, IConfiguration config, ServiceDef svc, CancellationToken ct)
    {
        var url = config[svc.ConfigKey];
        if (string.IsNullOrWhiteSpace(url))
            return new CheckResult(svc.Name, svc.Category, "Skipped", 0, "Not configured", null);

        var sw = Stopwatch.StartNew();
        string status, detail;
        string? body = null;

        try
        {
            // Read body now only when we need to parse it for version — avoids extra allocations otherwise
            var completion = svc.Version is VersionSource.ParseHealthBody
                ? HttpCompletionOption.ResponseContentRead
                : HttpCompletionOption.ResponseHeadersRead;

            using var resp = await client.GetAsync(url, completion, ct);
            sw.Stop();
            // 2xx = Healthy, 3xx = Healthy (service alive, redirecting — auto-redirect disabled),
            // 4xx = Degraded (service up but reporting errors), 5xx = Unhealthy
            status = resp.IsSuccessStatusCode || ((int)resp.StatusCode is >= 300 and < 400)
                ? "Healthy"
                : (int)resp.StatusCode >= 500 ? "Unhealthy" : "Degraded";
            detail = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";

            if (svc.Version is VersionSource.ParseHealthBody)
                body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CheckResult(svc.Name, svc.Category, "Unhealthy", (int)sw.ElapsedMilliseconds,
                ex.Message.Split('\n')[0].Trim(), null);
        }

        // Version is best-effort metadata — failure never affects the health status above
        var version = await TryGetVersionAsync(client, url, svc.Version, body, ct);
        return new CheckResult(svc.Name, svc.Category, status, (int)sw.ElapsedMilliseconds, detail, version);
    }

    private static async Task<string?> TryGetVersionAsync(
        HttpClient client, string healthUrl, VersionSource src, string? healthBody, CancellationToken ct)
    {
        try
        {
            return src switch
            {
                VersionSource.None => null,
                VersionSource.ParseHealthBody pb
                                           => ParseJsonField(healthBody, pb.Field),
                VersionSource.HmEndpoint => await FetchVersionUrlAsync(client, healthUrl, "/version", "version", ct),
                VersionSource.SecondaryCall s
                                           => await FetchVersionUrlAsync(client, healthUrl, s.Path, s.Field, ct),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> FetchVersionUrlAsync(
        HttpClient client, string healthUrl, string path, string jsonField, CancellationToken parentCt)
    {
        var versionUrl = ReplaceUrlPath(healthUrl, path);
        if (versionUrl is null) return null;

        // Tight 3-second budget for version probes — never block the health result
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        using var resp = await client.GetAsync(versionUrl, HttpCompletionOption.ResponseContentRead, cts.Token);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        return ParseJsonField(body, jsonField);
    }

    private static string? ReplaceUrlPath(string url, string newPath)
    {
        try
        {
            var b = new UriBuilder(url) { Path = newPath, Query = string.Empty };
            return b.Uri.ToString();
        }
        catch { return null; }
    }

    /// <summary>
    /// Extracts a string value from JSON by dot-notation path (e.g., "data.version").
    /// Returns null on any parse failure — callers should treat this as best-effort.
    /// </summary>
    private static string? ParseJsonField(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var parts = field.Split('.');
            JsonElement el = doc.RootElement;
            foreach (var part in parts)
            {
                if (!el.TryGetProperty(part, out el)) return null;
            }
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
        }
        catch { return null; }
    }

    // ── HTML renderer ─────────────────────────────────────────────────────────────────────────

    private static string BuildHtml(CheckResult[] results, string overall, DateTime now, string gatewayVersion)
    {
        var bannerColor = overall switch
        {
            "Healthy" => "#22c55e",
            "Degraded" => "#f59e0b",
            _ => "#ef4444",
        };

        var gwAge = now - GatewayStartTime;
        var gwAgeStr = FormatAge(gwAge);

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
                body { font-family: system-ui, sans-serif; background: #0f172a; color: #e2e8f0; padding: 2rem; max-width: 960px; margin: 0 auto; }
                h1 { font-size: 1.5rem; font-weight: 700; margin-bottom: .25rem; }
                .subtitle { color: #94a3b8; font-size: .875rem; margin-bottom: 1.5rem; }
                .subtitle .ver { color: #cbd5e1; font-weight: 600; }
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
                .ver-badge { font-family: ui-monospace, monospace; font-size: .75rem; color: #94a3b8; }
                .footer { margin-top: 1rem; color: #334155; font-size: .75rem; text-align: right; }
              </style>
            </head>
            <body>
              <h1>Platform Health</h1>
              <p class="subtitle">
                Platform <span class="ver">v{{WebUtility.HtmlEncode(gatewayVersion)}}</span>
                &nbsp;·&nbsp; Checked {{now:yyyy-MM-dd HH:mm:ss}} UTC
                &nbsp;·&nbsp; Auto-refreshes every 30 s
                &nbsp;·&nbsp; Gateway up {{WebUtility.HtmlEncode(gwAgeStr)}} (since {{GatewayStartTime:yyyy-MM-dd HH:mm}} UTC)
              </p>
              <div class="banner"><span class="dot"></span>{{overall}}</div>
              <table>
                <thead>
                  <tr>
                    <th>Component</th><th>Category</th><th>Status</th><th>Version</th><th>Latency</th><th>Pod Age</th><th>Detail</th>
                  </tr>
                </thead>
                <tbody>
            """);

        foreach (var r in results)
        {
            var latency = r.LatencyMs > 0 ? $"{r.LatencyMs} ms" : "—";
            var detail = WebUtility.HtmlEncode(r.Detail ?? string.Empty);
            var ver = r.Version is not null ? WebUtility.HtmlEncode(r.Version) : "<span class=\"muted\">—</span>";
            var podAge = r.PodStartTime.HasValue
                ? $"<span title=\"{WebUtility.HtmlEncode(r.PodStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC")}\">{WebUtility.HtmlEncode(FormatAge(now - r.PodStartTime.Value))}</span>"
                : "<span class=\"muted\">—</span>";
            sb.Append(CultureInfo.InvariantCulture, $$"""

                      <tr>
                        <td>{{WebUtility.HtmlEncode(r.Name)}}</td>
                        <td class="muted">{{WebUtility.HtmlEncode(r.Category)}}</td>
                        <td><span class="pill {{r.Status}}">{{r.Status}}</span></td>
                        <td class="ver-badge">{{ver}}</td>
                        <td class="muted">{{latency}}</td>
                        <td class="muted">{{podAge}}</td>
                        <td class="muted">{{detail}}</td>
                      </tr>
                """);
        }

        sb.Append(CultureInfo.InvariantCulture, $$"""

                  </tbody>
                </table>
                <p class="footer">HomeManagement Platform · cowgomu.net · cached 15 s</p>
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
        string? Detail,
        string? Version,
        DateTime? PodStartTime = null);
}
