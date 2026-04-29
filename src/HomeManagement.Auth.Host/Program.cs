using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Repositories;
using HomeManagement.Auth;
using HomeManagement.Auth.Host.Endpoints;
using HomeManagement.Core;
using HomeManagement.Data;
using HomeManagement.Data.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using System.Globalization;
using System.Threading.RateLimiting;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.AddHomeManagementSerilog("hm-auth");
builder.AddHomeManagementObservability("hm-auth");

// ── Configuration ──
var connectionString = builder.Configuration.GetConnectionString("HomeManagement")
    ?? throw new InvalidOperationException("Connection string 'HomeManagement' is required.");

// ── Services ──
builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
builder.Services.AddHomeManagementSqlServer(connectionString);
builder.Services.AddHomeManagementAuthRepositories();
builder.Services.AddHomeManagementAuth(builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddAuthorization();

// ── Rate limiting ──
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", policy =>
    {
        policy.PermitLimit = 10;
        policy.Window = TimeSpan.FromMinutes(1);
        policy.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.AddHealthChecks();

var app = builder.Build();

app.Services.GetRequiredService<IJwtTokenService>();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<IAuthDatabaseInitializer>().MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<AuthService>().EnsureInitializedAsync();
}

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

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ── Auth Endpoints ──
app.MapLoginEndpoints();
app.MapTokenEndpoints();
app.MapUserAdminEndpoints();

app.Run();

public partial class Program;
