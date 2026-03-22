using HomeManagement.AgentGateway.Host.Endpoints;
using HomeManagement.AgentGateway.Host.Services;
using HomeManagement.Core;
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
    .Enrich.WithProperty("Service", "hm-agent-gw")
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.With<SensitivePropertyEnricher>()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .WriteTo.Seq(context.Configuration["Seq:Url"] ?? "http://localhost:5341"));

// ── OpenTelemetry ──
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hm-agent-gw"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

// ── Services ──
builder.Services.AddSingleton<ApiKeyInterceptor>();
builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<ApiKeyInterceptor>();
});
builder.Services.AddSingleton<StandaloneAgentGatewayService>();
builder.Services.AddHealthChecks();

var app = builder.Build();

// ── Health ──
app.MapHealthChecks("/healthz");
app.MapGet("/readyz", () => Results.Ok("ready"));
app.MapPrometheusScrapingEndpoint();

// ── gRPC endpoint ──
app.MapGrpcService<AgentGatewayGrpcService>();
app.MapControlPlaneEndpoints();

app.Run();
