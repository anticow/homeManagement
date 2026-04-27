using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Auth;
using HomeManagement.Core;
using HomeManagement.Gateway;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using System.Globalization;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ──
builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.WithProperty("Service", "hm-gateway")
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.With<SensitivePropertyEnricher>()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .WriteTo.Seq(context.Configuration["Seq:Url"] ?? "http://localhost:5341"));

// ── OpenTelemetry ──
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hm-gateway"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

// ── Auth ──
builder.Services.AddHomeManagementAuth(options =>
    builder.Configuration.GetSection(AuthOptions.SectionName).Bind(options));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddAuthorization();

// ── YARP ──
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
builder.Services.AddHealthChecks();

// Named client used exclusively by PlatformHealthEndpoint — short timeout, no retry.
// Cert validation is bypassed: several platform services use self-signed cluster-internal
// certs that the pod's trust store doesn't include (ArgoCD, Grafana, etc.).
// Auto-redirect is disabled: following HTTP→HTTPS redirects re-triggers SSL errors for
// services that redirect from their plain-HTTP port to their HTTPS endpoint.
// The health check treats 3xx as "service is alive" (Healthy with redirect noted in Detail).
builder.Services.AddHttpClient("platform-health", c =>
    c.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        AllowAutoRedirect = false,
    });

var app = builder.Build();

app.MapHealthChecks("/healthz");
app.MapPrometheusScrapingEndpoint();

// Unauthenticated platform-wide health page (HTTP-only ingress, no credentials exposed)
PlatformHealthEndpoint.Map(app);

// ── Correlation ID + HTTP request logging ──
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(opts =>
    opts.GetLevel = (ctx, _, _) =>
        ctx.Request.Path.StartsWithSegments("/healthz") || ctx.Request.Path.StartsWithSegments("/platform-health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

app.UseAuthentication();
app.UseAuthorization();

app.MapReverseProxy();

app.Run();
