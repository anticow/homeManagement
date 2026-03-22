using HomeManagement.Auth;
using HomeManagement.Auth.Host.Endpoints;
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
    .Enrich.WithProperty("Service", "hm-auth")
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .WriteTo.Seq(context.Configuration["Seq:Url"] ?? "http://localhost:5341"));

// ── OpenTelemetry ──
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hm-auth"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

// ── Configuration ──
var connectionString = builder.Configuration.GetConnectionString("HomeManagement")
    ?? throw new InvalidOperationException("Connection string 'HomeManagement' is required.");

// ── Services ──
builder.Services.AddHomeManagementSqlServer(connectionString);
builder.Services.AddHomeManagementAuth(options =>
    builder.Configuration.GetSection(AuthOptions.SectionName).Bind(options));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddAuthorization();

builder.Services.AddHealthChecks();

var app = builder.Build();

app.Services.GetRequiredService<JwtTokenService>();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AuthService>().EnsureInitializedAsync();
}

// ── Health ──
app.MapHealthChecks("/healthz");
app.MapGet("/readyz", () => Results.Ok("ready"));
app.MapPrometheusScrapingEndpoint();

app.UseAuthentication();
app.UseAuthorization();

// ── Auth Endpoints ──
app.MapLoginEndpoints();
app.MapTokenEndpoints();
app.MapUserAdminEndpoints();

app.Run();

public partial class Program;
