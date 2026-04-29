using System.Globalization;
using HomeManagement.Abstractions.CrossCutting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;

namespace HomeManagement.Core;

public static class WebApplicationBuilderExtensions
{
    private const string SeqConfigKey = "Seq:Url";
    private const string SeqFallbackUrl = "http://localhost:5341";

    /// <summary>
    /// Configures Serilog with the standard HomeManagement enricher set and writes to Console + Seq.
    /// Seq URL is read from config key <c>Seq:Url</c>; falls back to <c>http://localhost:5341</c>.
    /// </summary>
    public static WebApplicationBuilder AddHomeManagementSerilog(
        this WebApplicationBuilder builder, string serviceName)
    {
        builder.Host.UseSerilog((context, services, config) => config
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.WithProperty("Service", serviceName)
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.With<SensitivePropertyEnricher>()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.Seq(context.Configuration[SeqConfigKey] ?? SeqFallbackUrl));
        return builder;
    }

    /// <summary>
    /// Registers OpenTelemetry with the standard HomeManagement resource, metrics (AspNetCore,
    /// HttpClient, Runtime, Prometheus), and an optional hook for service-specific meters.
    /// </summary>
    public static WebApplicationBuilder AddHomeManagementObservability(
        this WebApplicationBuilder builder,
        string serviceName,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                 .AddHttpClientInstrumentation()
                 .AddRuntimeInstrumentation()
                 .AddPrometheusExporter();
                configureMetrics?.Invoke(m);
            });
        return builder;
    }
}
