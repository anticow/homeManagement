using HomeManagement.Auth;
using HomeManagement.AI.Abstractions.Configuration;
using HomeManagement.Auditing;
using HomeManagement.Broker.Host.Endpoints;
using HomeManagement.Broker.Host.Hubs;
using HomeManagement.Core;
using HomeManagement.Data.SqlServer;
using HomeManagement.Automation;
using HomeManagement.Integration.Action1;
using HomeManagement.Integration.Prometheus;
using HomeManagement.Vault;
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
    .Enrich.WithProperty("Service", "hm-broker")
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.With<SensitivePropertyEnricher>()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .WriteTo.Seq(context.Configuration["Seq:Url"] ?? "http://localhost:5341"));

// ── OpenTelemetry ──
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hm-broker"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("HomeManagement.Automation")
        .AddPrometheusExporter());

// ── Configuration ──
var connectionString = builder.Configuration.GetConnectionString("HomeManagement")
    ?? throw new InvalidOperationException("Connection string 'HomeManagement' is required.");

var dataDirectory = builder.Configuration["DataDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomeManagement");

// ── Services ──
builder.Services.AddHomeManagementSqlServer(connectionString);
builder.Services.AddHomeManagement(dataDirectory);
builder.Services.AddHomeManagementLogging(dataDirectory);
builder.Services.AddAction1Integration(builder.Configuration);
builder.Services.AddPrometheusIntegration(builder.Configuration);
builder.Services.AddHomeManagementAuth(options =>
    builder.Configuration.GetSection(AuthOptions.SectionName).Bind(options));
builder.Services
    .AddOptions<AiOptions>()
    .Bind(builder.Configuration.GetSection(AiOptions.SectionName))
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Provider),
        "AI:Provider is required when AI is enabled.")
    .Validate(options =>
        !options.Enabled
        || !options.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
        || Uri.TryCreate(options.Ollama.BaseUrl, UriKind.Absolute, out _),
        "AI:Ollama:BaseUrl must be a valid absolute URI when Provider is Ollama.")
    .Validate(options =>
        !options.Enabled
        || !options.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(options.Ollama.Model),
        "AI:Ollama:Model is required when Provider is Ollama.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AutomationOptions>()
    .Bind(builder.Configuration.GetSection(AutomationOptions.SectionName))
    .Validate(options =>
        !options.Enabled || builder.Configuration.GetValue<bool>("AI:Enabled"),
        "Automation:Enabled requires AI:Enabled=true in the current Phase 0 baseline.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AuditOptions>()
    .Bind(builder.Configuration.GetSection(AuditOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.HmacKey),
        "Audit:HmacKey is required. Generate with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))")
    .ValidateOnStart();

builder.Services
    .AddOptions<VaultOptions>()
    .Bind(builder.Configuration.GetSection(VaultOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.StoragePath),
        "Vault:StoragePath is required.")
    .ValidateOnStart();

// ── JWT Authentication ──
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Token validation parameters are provided by JwtTokenService at runtime
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // Support SignalR hub auth via query string only for transports
                // that cannot reliably send the Authorization header after negotiate.
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                var acceptsEventStream = context.Request.Headers.Accept.Any(
                    value => value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true);
                var canUseQueryToken = context.HttpContext.WebSockets.IsWebSocketRequest || acceptsEventStream;

                if (!string.IsNullOrEmpty(accessToken)
                    && path.StartsWithSegments("/hubs/events")
                    && canUseQueryToken)
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();

var app = builder.Build();

// ── Configure JWT validation from JwtTokenService ──
// The JwtBearer middleware picks up parameters via PostConfigure
app.Services.GetRequiredService<JwtTokenService>();

// ── Security headers ──
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("X-Frame-Options", "DENY");
    ctx.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    ctx.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
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

// ── Auth middleware ──
app.UseAuthentication();
app.UseAuthorization();

// ── Domain API Endpoints ──
app.MapMachineEndpoints();
app.MapAgentEndpoints();
app.MapPatchingEndpoints();
app.MapServiceEndpoints();
app.MapJobEndpoints();
app.MapCredentialEndpoints();
app.MapAuditEndpoints();
app.MapAutomationEndpoints();
app.MapAction1WebhookEndpoints();

// ── SignalR Hub ──
app.MapHub<EventHub>("/hubs/events").RequireAuthorization();

// ── Database initialization ──
await ServiceRegistration.InitializeDatabaseAsync(app.Services);

app.Run();

public partial class Program;
