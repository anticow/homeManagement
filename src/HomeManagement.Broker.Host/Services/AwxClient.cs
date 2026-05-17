namespace HomeManagement.Broker.Host.Services;

public sealed class AwxOptions
{
    public const string Section = "Awx";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed record AwxJobTemplate(int Id, string Name, string Description, string PlaybookName, int LastJobRun, string Status);
public sealed record AwxJob(int Id, string Name, string Status, string JobType, DateTime? Started, DateTime? Finished, bool Failed, string LaunchedBy);

public sealed class AwxClient
{
    private static readonly Action<ILogger, Exception?> LogFetchJobTemplatesFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, nameof(LogFetchJobTemplatesFailed)), "AWX: failed to fetch job templates");
    private static readonly Action<ILogger, Exception?> LogFetchRecentJobsFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, nameof(LogFetchRecentJobsFailed)), "AWX: failed to fetch recent jobs");
    private static readonly Action<ILogger, int, int, Exception?> LogLaunchTemplateStatus =
        LoggerMessage.Define<int, int>(LogLevel.Warning, new EventId(3, nameof(LogLaunchTemplateStatus)), "AWX: launch template {Id} returned {Status}");
    private static readonly Action<ILogger, int, Exception?> LogLaunchTemplateFailed =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(4, nameof(LogLaunchTemplateFailed)), "AWX: failed to launch job template {Id}");

    private readonly HttpClient _http;
    private readonly ILogger<AwxClient> _logger;

    public AwxClient(HttpClient http, ILogger<AwxClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AwxJobTemplate>> GetJobTemplatesAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("api/v2/job_templates/?order_by=name&page_size=100", ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var results = doc.RootElement.GetProperty("results");
            var list = new List<AwxJobTemplate>();
            foreach (var el in results.EnumerateArray())
            {
                list.Add(new AwxJobTemplate(
                    Id: el.GetProperty("id").GetInt32(),
                    Name: el.GetProperty("name").GetString() ?? "",
                    Description: el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    PlaybookName: el.TryGetProperty("playbook", out var p) ? p.GetString() ?? "" : "",
                    LastJobRun: el.TryGetProperty("last_job_run", out var ljr) && ljr.ValueKind != System.Text.Json.JsonValueKind.Null ? (int)(ljr.GetDateTime() - DateTime.UnixEpoch).TotalSeconds : 0,
                    Status: el.TryGetProperty("status", out var s) ? s.GetString() ?? "" : ""
                ));
            }
            return list;
        }
        catch (Exception ex)
        {
            LogFetchJobTemplatesFailed(_logger, ex);
            return [];
        }
    }

    public async Task<IReadOnlyList<AwxJob>> GetRecentJobsAsync(int limit = 50, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"api/v2/jobs/?order_by=-id&page_size={limit}", ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var results = doc.RootElement.GetProperty("results");
            var list = new List<AwxJob>();
            foreach (var el in results.EnumerateArray())
            {
                var launchedBy = "";
                if (el.TryGetProperty("summary_fields", out var sf) && sf.TryGetProperty("launched_by", out var lb))
                {
                    launchedBy = lb.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
                }

                list.Add(new AwxJob(
                    Id: el.GetProperty("id").GetInt32(),
                    Name: el.GetProperty("name").GetString() ?? "",
                    Status: el.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                    JobType: el.TryGetProperty("job_type", out var jt) ? jt.GetString() ?? "" : "",
                    Started: el.TryGetProperty("started", out var st) && st.ValueKind != System.Text.Json.JsonValueKind.Null ? st.GetDateTime() : null,
                    Finished: el.TryGetProperty("finished", out var fin) && fin.ValueKind != System.Text.Json.JsonValueKind.Null ? fin.GetDateTime() : null,
                    Failed: el.TryGetProperty("failed", out var f) && f.GetBoolean(),
                    LaunchedBy: launchedBy
                ));
            }
            return list;
        }
        catch (Exception ex)
        {
            LogFetchRecentJobsFailed(_logger, ex);
            return [];
        }
    }

    public async Task<int?> LaunchJobTemplateAsync(int templateId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsync($"api/v2/job_templates/{templateId}/launch/", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode)
            {
                LogLaunchTemplateStatus(_logger, templateId, (int)resp.StatusCode, null);
                return null;
            }

            using var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : null;
        }
        catch (Exception ex)
        {
            LogLaunchTemplateFailed(_logger, templateId, ex);
            return null;
        }
    }
}
