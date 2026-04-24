using HomeManagement.Abstractions.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Prometheus;

/// <summary>
/// DI registration for the Prometheus integration.
/// Call <c>services.AddPrometheusIntegration(configuration)</c> in Broker Program.cs.
///
/// When <see cref="PrometheusOptions.Enabled"/> is false,
/// <see cref="DisabledPrometheusEndpointStateProvider"/> is registered instead
/// and no HTTP client is created.
/// </summary>
public static class PrometheusIntegrationRegistration
{
    public static IServiceCollection AddPrometheusIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<PrometheusOptions>(configuration.GetSection(PrometheusOptions.Section));

        var options = configuration.GetSection(PrometheusOptions.Section).Get<PrometheusOptions>()
                      ?? new PrometheusOptions();

        if (!options.Enabled)
        {
            services.AddSingleton<PrometheusEndpointStateProvider, DisabledPrometheusEndpointStateProvider>();
            services.AddSingleton<IEndpointStateProvider, DisabledPrometheusEndpointStateProvider>();
            return services;
        }

        // ── Typed HTTP client ─────────────────────────────────────────────────
        services.AddHttpClient<PrometheusClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<PrometheusOptions>>().Value;
            client.BaseAddress = new Uri(opts.Url.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(opts.QueryTimeoutSeconds + 5); // outer > inner timeout
        });

        // ── State provider ────────────────────────────────────────────────────
        services.AddScoped<PrometheusEndpointStateProvider>();
        services.AddScoped<IEndpointStateProvider>(sp =>
            sp.GetRequiredService<PrometheusEndpointStateProvider>());

        return services;
    }
}

/// <summary>
/// No-op state provider used when Prometheus integration is disabled.
/// All queries return safe defaults — service state Unknown, endpoint offline.
/// </summary>
internal sealed class DisabledPrometheusEndpointStateProvider : PrometheusEndpointStateProvider
{
    public DisabledPrometheusEndpointStateProvider()
        : base(null!, Microsoft.Extensions.Options.Options.Create(new PrometheusOptions()), null!) { }

    public override Task<HomeManagement.Abstractions.ServiceState> GetServiceStateAsync(
        string hostname, string serviceName, HomeManagement.Abstractions.OsType osType, CancellationToken ct = default) =>
        Task.FromResult(HomeManagement.Abstractions.ServiceState.Unknown);

    public override Task<System.Collections.Generic.IReadOnlyList<Models.EndpointServiceState>> GetAllServiceStatesAsync(
        string hostname, HomeManagement.Abstractions.OsType osType, CancellationToken ct = default) =>
        Task.FromResult<System.Collections.Generic.IReadOnlyList<Models.EndpointServiceState>>(
            System.Array.Empty<Models.EndpointServiceState>());

    public override Task<Models.EndpointAvailability> GetEndpointAvailabilityAsync(
        string hostname, CancellationToken ct = default) =>
        Task.FromResult(new Models.EndpointAvailability(hostname, false, DateTime.UtcNow));

    public override Task<Models.EndpointMetrics> GetEndpointMetricsAsync(
        string hostname, HomeManagement.Abstractions.OsType osType, CancellationToken ct = default) =>
        Task.FromResult(new Models.EndpointMetrics(
            hostname, null, null, null, null, null, null, DateTime.UtcNow));
}
