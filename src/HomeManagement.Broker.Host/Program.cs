using HomeManagement.Auth;
using HomeManagement.Broker.Host.Endpoints;
using HomeManagement.Broker.Host.Hubs;
using HomeManagement.Core;
using HomeManagement.Data.SqlServer;
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
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .WriteTo.Seq(context.Configuration["Seq:Url"] ?? "http://localhost:5341"));

// ── OpenTelemetry ──
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hm-broker"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
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
builder.Services.AddHomeManagementAuth(options =>
    builder.Configuration.GetSection(AuthOptions.SectionName).Bind(options));

// ── JWT Authentication ──
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Token validation parameters are provided by JwtTokenService at runtime
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                // Support SignalR hub auth via query string
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
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

// ── SignalR Hub ──
app.MapHub<EventHub>("/hubs/events");

// ── Database initialization ──
await ServiceRegistration.InitializeDatabaseAsync(app.Services);

app.Run();

public partial class Program;
