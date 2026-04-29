using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.AgentGateway.Host.Endpoints;
using HomeManagement.AgentGateway.Host.Services;
using HomeManagement.Core;
using Serilog;
using System.Globalization;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.AddHomeManagementSerilog("hm-agent-gw");
builder.AddHomeManagementObservability("hm-agent-gw");

// ── Services ──
builder.Services.AddOptions<AgentGatewayHostOptions>()
    .BindConfiguration(AgentGatewayHostOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<AgentApiKeyValidator>();
builder.Services.AddSingleton<IAgentApiKeyValidator>(sp => sp.GetRequiredService<AgentApiKeyValidator>());
builder.Services.AddGrpc();
builder.Services.AddSingleton<StandaloneAgentGatewayService>();
builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
builder.Services.AddHealthChecks();

var app = builder.Build();

// ── Security headers ──
app.UseHomeManagementSecurityHeaders();

// ── Correlation ID + HTTP request logging ──
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(opts =>
    opts.GetLevel = (ctx, _, _) =>
        ctx.Request.Path.StartsWithSegments("/healthz") || ctx.Request.Path.StartsWithSegments("/readyz")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

// ── Health ──
app.UseHomeManagementHealthEndpoints();

// ── gRPC endpoint ──
app.MapGrpcService<AgentGatewayGrpcService>();
app.MapControlPlaneEndpoints();

app.Run();
