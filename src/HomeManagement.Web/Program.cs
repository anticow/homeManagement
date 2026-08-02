using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Web.Services;
using HomeManagement.Core;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Refit;
using Serilog;
using System.Globalization;
using System.Text.Json;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.AddHomeManagementSerilog("hm-web");
builder.AddHomeManagementObservability("hm-web");

// ── Services ──
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Broker API client (Refit) ──
var brokerBaseUrl = builder.Configuration["BrokerApi:BaseUrl"]
    ?? "http://localhost:8082";
var authBaseUrl = builder.Configuration["AuthApi:BaseUrl"]
    ?? "http://localhost:8083";

builder.Services.AddScoped<ServerSessionState>();
builder.Services.AddScoped<WebSessionAuthService>();
builder.Services.AddScoped<IWebSessionAuthService>(sp => sp.GetRequiredService<WebSessionAuthService>());

builder.Services
    .AddRefitClient<IAuthApi>(new RefitSettings
    {
        ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web))
    })
    .ConfigureHttpClient(c => c.BaseAddress = new Uri(authBaseUrl));

builder.Services.AddHttpClient(BrokerApiClient.HttpClientName, c =>
{
    c.BaseAddress = new Uri(brokerBaseUrl);
    // 60s covers the worst-case broker fan-out (e.g. pending-patches across many machines)
    // while still surfacing errors rather than leaving the UI in perpetual "Loading…" state.
    c.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddScoped<IBrokerApi, BrokerApiClient>();

builder.Services.AddHttpClient(AdminApiClient.HttpClientName, c =>
    c.BaseAddress = new Uri(authBaseUrl));
builder.Services.AddScoped<IAdminApi, AdminApiClient>();

// ── Auth state ──
builder.Services.AddScoped<AuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(
    sp => sp.GetRequiredService<AuthStateProvider>());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
    });
builder.Services.AddAuthorizationCore();

// ── SignalR client for real-time events ──
builder.Services.AddScoped<EventHubClient>();

builder.Services.AddHealthChecks();
builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();

// ── Grafana embedding configuration ──
builder.Services.Configure<GrafanaOptions>(
    builder.Configuration.GetSection(GrafanaOptions.SectionName));

// ── Data Protection — persist keys to file system so antiforgery tokens survive pod restarts ──
var dpKeyPath = builder.Configuration["DataProtection:KeyStorePath"];
var dpBuilder = builder.Services.AddDataProtection()
    .SetApplicationName("homemanagement");
if (!string.IsNullOrWhiteSpace(dpKeyPath))
{
    Directory.CreateDirectory(dpKeyPath);
    dpBuilder.PersistKeysToFileSystem(new System.IO.DirectoryInfo(dpKeyPath));
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// ── Security headers ──
app.UseHomeManagementSecurityHeaders();

// ── Correlation ID + exception handling + HTTP request logging ──
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseHomeManagementExceptionHandler();
app.UseSerilogRequestLogging(opts =>
    opts.GetLevel = (ctx, _, _) =>
        ctx.Request.Path.StartsWithSegments("/healthz")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();

// ── Health ──
app.MapHealthChecks("/healthz");
app.MapGet("/version", () => new { version = Environment.GetEnvironmentVariable("APP_VERSION") ?? "unknown" })
    .RequireAuthorization().ExcludeFromDescription();
app.MapPrometheusScrapingEndpoint();

app.MapRazorComponents<HomeManagement.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
