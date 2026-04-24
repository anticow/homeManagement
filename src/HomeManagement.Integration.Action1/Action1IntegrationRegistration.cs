using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// DI registration for the Action1 integration.
/// Call <c>services.AddAction1Integration(configuration)</c> in Broker Program.cs,
/// then <c>app.MapAction1WebhookEndpoints()</c> after <c>app.Build()</c>.
///
/// When <see cref="Action1Options.Enabled"/> is false, <see cref="DisabledAction1PatchService"/>
/// is registered instead and no HTTP client or sync job is created.
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
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ApiKey),
                "Action1:ApiKey is required when Action1:Enabled is true. " +
                "Set via environment variable Action1__ApiKey or Kubernetes secret.")
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.OrganizationId),
                "Action1:OrganizationId is required when Action1:Enabled is true.")
            .ValidateOnStart();

        // Read options eagerly to decide which implementation to register.
        var options = configuration.GetSection(Action1Options.Section).Get<Action1Options>()
                      ?? new Action1Options();

        if (!options.Enabled)
        {
            services.AddSingleton<IPatchService, DisabledAction1PatchService>();
            return services;
        }

        // ── Typed HTTP client ─────────────────────────────────────────────────
        services.AddHttpClient<Action1Client>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<Action1Options>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {opts.ApiKey}");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        // ── Core service ──────────────────────────────────────────────────────
        services.AddScoped<IPatchService, Action1PatchService>();

        // ── Reconciliation sync job ───────────────────────────────────────────
        services.AddQuartz(q =>
        {
            q.AddJob<Action1SyncJob>(opts =>
                opts.WithIdentity(Action1SyncJob.Key).StoreDurably());

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
}

/// <summary>
/// No-op patch service used when the Action1 integration is disabled.
/// All operations return empty results, allowing the system to run without Action1.
/// </summary>
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
