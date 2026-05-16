using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// DI registration for the Action1 integration.
///
/// Call services.AddAction1Integration(configuration) in Broker Program.cs,
/// then app.MapAction1BrokerEndpoints() and app.MapAction1WebhookEndpoints()
/// after app.Build().
///
/// Authentication: OAuth2 client_credentials (ClientId + ClientSecret).
/// When Enabled = false, DisabledAction1PatchService is registered and
/// Action1Client is still available for broker endpoints (they return 503).
/// </summary>
public static class Action1IntegrationRegistration
{
    public static IServiceCollection AddAction1Integration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<Action1Options>()
            .Bind(configuration.GetSection(Action1Options.Section))
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ClientId),
                "Action1:ClientId is required when Action1:Enabled is true.")
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ClientSecret),
                "Action1:ClientSecret is required when Action1:Enabled is true.")
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.OrganizationId),
                "Action1:OrganizationId is required when Action1:Enabled is true.")
            .ValidateOnStart();

        // Action1Client is always registered (even when disabled) so broker endpoints
        // can inject it unconditionally and return 503 when the integration is off.
        services.AddHttpClient<Action1Client>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<Action1Options>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
            // Auth header is set per-request by Action1Client via OAuth2 token flow.
            // 30s is generous for the Action1 API; prevents the UI from hanging indefinitely
            // if Action1 is unreachable or a path is wrong.
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        var options = ReadOptions<Action1Options>(configuration, Action1Options.Section);

        if (!options.Enabled)
        {
            services.AddSingleton<IPatchService, DisabledAction1PatchService>();
            return services;
        }

        services.AddScoped<IPatchService, Action1PatchService>();

        // Schedule sync: ensures HM-configured automation schedules exist in Action1 on startup
        services.AddSingleton<Action1ScheduleSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<Action1ScheduleSyncService>());

        services.AddQuartz(q =>
        {
            q.AddJob<Action1SyncJob>(opts => opts.WithIdentity(Action1SyncJob.Key).StoreDurably());
            q.AddTrigger(t => t
                .ForJob(Action1SyncJob.Key)
                .WithIdentity("action1-sync-trigger", "homemanagement-integrations")
                .StartNow()
                .WithSimpleSchedule(s => s
                    .WithIntervalInMinutes(options.SyncIntervalMinutes)
                    .RepeatForever()));
        });

        return services;
    }

    private static TOptions ReadOptions<TOptions>(IConfiguration configuration, string section)
        where TOptions : new()
        => configuration.GetSection(section).Get<TOptions>() ?? new TOptions();
}

/// <summary>No-op patch service used when the Action1 integration is disabled.</summary>
internal sealed class DisabledAction1PatchService : IPatchService
{
    public Task<IReadOnlyList<PatchInfo>> DetectAsync(
        MachineTarget target, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PatchInfo>>(Array.Empty<PatchInfo>());

    public async IAsyncEnumerable<PatchInfo> DetectStreamAsync(
        MachineTarget target,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<PatchResult> ApplyAsync(
        MachineTarget target,
        IReadOnlyList<PatchInfo> patches,
        PatchOptions options,
        CancellationToken ct = default) =>
        Task.FromResult(new PatchResult(target.MachineId, 0, 0,
            Array.Empty<PatchOutcome>(), false, TimeSpan.Zero));

    public Task<PatchResult> VerifyAsync(
        MachineTarget target,
        IReadOnlyList<string> patchIds,
        CancellationToken ct = default) =>
        Task.FromResult(new PatchResult(target.MachineId, 0, 0,
            Array.Empty<PatchOutcome>(), false, TimeSpan.Zero));

    public Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(
        Guid machineId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PatchHistoryEntry>>(Array.Empty<PatchHistoryEntry>());

    public Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(
        MachineTarget target, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InstalledPatch>>(Array.Empty<InstalledPatch>());
}
