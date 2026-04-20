using HomeManagement.Web.Services;
using HomeManagement.Core;
using Microsoft.AspNetCore.Components.Authorization;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Refit;
using Serilog;
using System.Globalization;
using System.Text.Json;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ──
builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.WithProperty("Service", "hm-web")
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.With<SensitivePropertyEnricher>()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .WriteTo.Seq(context.Configuration["Seq:Url"] ?? "http://localhost:5341"));

// ── OpenTelemetry ──
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("hm-web"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

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
    c.BaseAddress = new Uri(brokerBaseUrl));
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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// ── Security headers ──
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("X-Frame-Options", "DENY");
    ctx.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    ctx.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
    await next();
});

app.UseStaticFiles();
app.UseAntiforgery();

// ── Health ──
app.MapHealthChecks("/healthz");
app.MapPrometheusScrapingEndpoint();

app.MapRazorComponents<HomeManagement.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
