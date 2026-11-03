using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.AgentGateway.Host.Endpoints;
using HomeManagement.AgentGateway.Host.Services;
using HomeManagement.Core;
using Serilog;
using System.Globalization;

var bootstrapExit = AgentEnrollmentBootstrap.Run(args, Console.Out, Console.Error);
if (bootstrapExit is { } exitCode)
{
    Environment.ExitCode = exitCode;
    return;
}

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.AddHomeManagementSerilog("hm-agent-gw");
builder.AddHomeManagementObservability("hm-agent-gw");

// ── Services ──
builder.Services.AddOptions<AgentGatewayHostOptions>()
    .BindConfiguration(AgentGatewayHostOptions.SectionName)
    .Validate(
        AgentGatewayHostOptions.HasValidControlPlaneApiKey,
        $"AgentGateway:ApiKey must be a nontrivial Base64-encoded key containing exactly {AgentGatewayHostOptions.MinimumApiKeyBytes} decoded bytes.")
    .ValidateOnStart();
builder.Services.AddGatewayAgentAuthority(builder.Configuration);
builder.Services.AddGrpc();
builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.InitializeGatewayAgentAuthority();

// ── Security headers ──
app.UseHsts();
app.UseHomeManagementSecurityHeaders();

// ── Correlation ID + exception handling + HTTP request logging ──
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseHomeManagementExceptionHandler();
app.UseSerilogRequestLogging(opts =>
    opts.GetLevel = (ctx, _, _) =>
        ctx.Request.Path.StartsWithSegments("/healthz") || ctx.Request.Path.StartsWithSegments("/readyz")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);
app.UseAgentEnrollmentBoundary();
app.UseControlPlaneApiKeyAuthentication();

// ── Health ──
app.UseHomeManagementHealthEndpoints(taggedReadiness: app.Services.GetRequiredService<AgentEnrollmentMode>().Enabled);

// ── gRPC endpoint ──
app.MapGrpcService<AgentGatewayGrpcService>();
app.MapControlPlaneEndpoints();
app.MapAgentEnrollmentEndpoints();

app.Run();
