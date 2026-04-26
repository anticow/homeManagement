using HomeManagement.Abstractions.CrossCutting;
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
builder.Services.AddSingleton<AgentApiKeyValidator>();
builder.Services.AddSingleton<IAgentApiKeyValidator>(sp => sp.GetRequiredService<AgentApiKeyValidator>());
builder.Services.AddGrpc();
builder.Services.AddSingleton<StandaloneAgentGatewayService>();
builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
builder.Services.AddHealthChecks();

var app = builder.Build();

// ── Security headers ──
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("X-Frame-Options", "DENY");
    ctx.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

// ── Correlation ID + HTTP request logging ──
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(opts =>
    opts.GetLevel = (ctx, _, _) =>
        ctx.Request.Path.StartsWithSegments("/healthz") || ctx.Request.Path.StartsWithSegments("/readyz")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

// ── Health ──
app.MapHealthChecks("/healthz");
app.MapGet("/readyz", () => Results.Ok("ready"));
app.MapPrometheusScrapingEndpoint();

// ── gRPC endpoint ──
app.MapGrpcService<AgentGatewayGrpcService>();
app.MapControlPlaneEndpoints();

app.Run();
