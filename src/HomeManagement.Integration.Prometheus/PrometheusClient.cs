using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeManagement.Integration.Prometheus.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Prometheus;

/// <summary>
/// HTTP client for querying the Prometheus HTTP API.
/// Registered as a typed HttpClient. Base address is set from <see cref="PrometheusOptions.Url"/>.
/// </summary>
public sealed class PrometheusClient
{
    private readonly HttpClient _http;
    private readonly PrometheusOptions _options;
    private readonly ILogger<PrometheusClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PrometheusClient(
        HttpClient http,
        IOptions<PrometheusOptions> options,
        ILogger<PrometheusClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Executes an instant PromQL vector query.
    /// Returns an empty list on error rather than throwing, so callers degrade gracefully.
    /// </summary>
    public async Task<IReadOnlyList<PrometheusVector>> QueryAsync(
        string promql,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.QueryTimeoutSeconds));

        var url = $"api/v1/query?query={Uri.EscapeDataString(promql)}";
        _logger.LogDebug("Prometheus query: {Query}", promql);

        try
        {
            var response = await _http.GetFromJsonAsync<PrometheusApiResponse<PrometheusQueryData>>(
                url, JsonOptions, cts.Token);

            if (response is null || response.Status != "success" || response.Data is null)
            {
                _logger.LogWarning("Prometheus query returned non-success: {Error}", response?.Error);
                return [];
            }

            return response.Data.Result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Prometheus query timed out after {Seconds}s: {Query}",
                _options.QueryTimeoutSeconds, promql);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prometheus query failed: {Query}", promql);
            return [];
        }
    }

    /// <summary>
    /// Convenience: query a single scalar value from the first vector result.
    /// Returns null if the query returns no results or the value is non-numeric.
    /// </summary>
    public async Task<double?> QueryScalarAsync(string promql, CancellationToken ct = default)
    {
        var results = await QueryAsync(promql, ct);
        return results.Count > 0 ? results[0].Value.AsDouble() : null;
    }

    /// <summary>
    /// Convenience: query all vector results and return metric labels + values.
    /// Returns empty list if Prometheus is unreachable.
    /// </summary>
    public async Task<IReadOnlyList<(IReadOnlyDictionary<string, string> Labels, double Value)>>
        QueryVectorsAsync(string promql, CancellationToken ct = default)
    {
        var results = await QueryAsync(promql, ct);
        return results
            .Select(r => (r.Metric, r.Value.AsDouble() ?? 0.0))
            .ToList()
            .AsReadOnly();
    }
}
