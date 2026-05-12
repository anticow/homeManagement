using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// Hosted service that synchronises HM-configured patch automation schedules
/// to Action1 on broker startup.
///
/// Strategy A — Severity-Based Deferred Schedule Management:
///   For each rule in Action1:ScheduleSync:Rules the service ensures an
///   automation schedule exists in Action1 with the correct name, severity
///   filter, approval mode, and defer-day soak period.
///
/// Matching is done by the "homeManagement: {Name}" prefix convention so HM
/// never touches manually created schedules.
///
/// On each startup the service will:
///   1. Fetch all existing schedules from Action1.
///   2. For each configured rule:
///      a. If a schedule with the matching name exists — verify its key params
///         and PATCH if they differ (defer_days, update_approval, settings).
///      b. If no matching schedule exists — POST to create it.
///   3. Log a full discovery summary so admins can confirm sync results.
/// </summary>
public sealed class Action1ScheduleSyncService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<Action1Options> _options;
    private readonly ILogger<Action1ScheduleSyncService> _logger;

    public Action1ScheduleSyncService(
        IServiceScopeFactory scopeFactory,
        IOptions<Action1Options> options,
        ILogger<Action1ScheduleSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = _options.Value;

        if (!opts.Enabled)
        {
            _logger.LogDebug("Action1ScheduleSync: integration is disabled, skipping.");
            return;
        }

        if (!opts.ScheduleSync.Enabled)
        {
            _logger.LogDebug("Action1ScheduleSync: ScheduleSync.Enabled is false, skipping.");
            return;
        }

        if (opts.ScheduleSync.Rules.Count == 0)
        {
            _logger.LogWarning("Action1ScheduleSync: Enabled but no Rules configured. Add rules to Action1:ScheduleSync:Rules.");
            return;
        }

        try
        {
            await SyncSchedulesAsync(opts, cancellationToken);
        }
        catch (Exception ex)
        {
            // Sync failure must not prevent broker startup.
            _logger.LogError(ex, "Action1ScheduleSync: startup sync failed — schedules may be out of sync. Will retry on next startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── Core sync logic ───────────────────────────────────────────────────────

    private async Task SyncSchedulesAsync(Action1Options opts, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<Action1Client>();

        _logger.LogInformation("Action1ScheduleSync: syncing {Count} configured rule(s) to Action1.",
            opts.ScheduleSync.Rules.Count);

        // Fetch all existing schedules once
        var existing = await client.GetSchedulesAsync(ct);
        _logger.LogInformation("Action1ScheduleSync: found {Count} existing schedule(s) in Action1.",
            existing.Count);

        // Log discovery so admins can inspect the settings format of existing schedules
        foreach (var s in existing.Where(s => !s.IsSystem))
        {
            _logger.LogDebug(
                "Action1ScheduleSync: existing schedule '{Name}' id={Id} settings={Settings} last_run={LastRun} next_run={NextRun}",
                s.Name, s.Id, s.Settings, s.LastRun, s.NextRun);
        }

        // Build lookup by full name (case-insensitive)
        var existingByName = existing
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var rule in opts.ScheduleSync.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                _logger.LogWarning("Action1ScheduleSync: skipping rule with empty Name.");
                continue;
            }

            await SyncRuleAsync(client, rule, existingByName, ct);
        }

        _logger.LogInformation("Action1ScheduleSync: sync complete.");
    }

    private async Task SyncRuleAsync(
        Action1Client client,
        ManagedScheduleRule rule,
        Dictionary<string, Models.Action1Schedule> existingByName,
        CancellationToken ct)
    {
        existingByName.TryGetValue(rule.FullName, out var existing);

        if (existing is null)
        {
            _logger.LogInformation(
                "Action1ScheduleSync: schedule '{FullName}' not found — creating.",
                rule.FullName);

            var body = BuildScheduleBody(rule);
            var created = await client.CreateScheduleAsync(body, ct);

            if (created is null)
                _logger.LogError("Action1ScheduleSync: failed to create schedule '{FullName}'.", rule.FullName);
            else
                _logger.LogInformation("Action1ScheduleSync: created schedule '{FullName}' id={Id}.", rule.FullName, created);

            return;
        }

        // Check if the schedule needs patching
        var existingParams = existing.Actions is { Count: > 0 } acts ? acts[0].Params : null;
        var existingApproval = existingParams?.UpdateApproval ?? "";
        var expectedApproval = rule.AutoApprove ? "auto" : "manual";
        var existingDefer = existingParams?.AutomaticApprovalDelayDays ?? -1;
        var existingSettings = existing.Settings ?? "";
        var needsPatch =
            !string.Equals(existingApproval, expectedApproval, StringComparison.OrdinalIgnoreCase) ||
            existingDefer != rule.DeferDays ||
            !string.Equals(existingSettings, rule.ScheduleSettings, StringComparison.OrdinalIgnoreCase);

        if (!needsPatch)
        {
            _logger.LogInformation(
                "Action1ScheduleSync: schedule '{FullName}' id={Id} is up to date.",
                rule.FullName, existing.Id);
            return;
        }

        _logger.LogInformation(
            "Action1ScheduleSync: schedule '{FullName}' id={Id} is out of sync — updating (approval={Approval}, defer={Defer}d, settings={Settings}).",
            rule.FullName, existing.Id, expectedApproval, rule.DeferDays, rule.ScheduleSettings);

        var patch = BuildScheduleBody(rule);
        var ok = await client.UpdateScheduleAsync(existing.Id, patch, ct);

        if (!ok)
            _logger.LogError("Action1ScheduleSync: failed to update schedule '{FullName}' id={Id}.", rule.FullName, existing.Id);
    }

    // ── Body builder ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the POST/PATCH body for an Action1 automation schedule from a <see cref="ManagedScheduleRule"/>.
    ///
    /// The body follows the format used by the PSAction1 module's DeferredRemediation template.
    ///
    /// Filter logic:
    ///   - Empty Severities → "All" scope (install everything)
    ///   - One or more severities → "Matching" scope with severity filter values
    ///
    /// The packages field uses [{"default": "default"}] for scope=All, or a
    /// filter object for scope=Matching — both are the Action1-native convention.
    /// </summary>
    internal static object BuildScheduleBody(ManagedScheduleRule rule)
    {
        var hasSeverityFilter = rule.Severities.Count > 0;
        var scope = hasSeverityFilter ? "Matching" : "All";

        // Severity filter objects are {include: true, value: "Critical", parameter: "severity"}
        var filterList = hasSeverityFilter
            ? rule.Severities.Select(s => (object)new
            {
                include = true,
                value = s,
                parameter = "severity"
            }).ToArray()
            : Array.Empty<object>();

        var packages = hasSeverityFilter
            ? (object)new[] { new { filter = "filter" } }   // placeholder for filter-based
            : new[] { new Dictionary<string, string> { ["default"] = "default" } };

        var displaySummary = hasSeverityFilter
            ? $"homeManagement auto-patch: {string.Join(", ", rule.Severities)} (soak {rule.DeferDays}d)"
            : $"homeManagement auto-patch: all severities (soak {rule.DeferDays}d)";

        return new
        {
            name = rule.FullName,
            settings = rule.ScheduleSettings,
            retry_minutes = rule.RetryMinutes,
            endpoints = new[]
            {
                new { id = "ALL", type = "EndpointGroup" }
            },
            actions = new[]
            {
                new
                {
                    name = "Deploy Update",
                    template_id = "deploy_update",
                    @params = new
                    {
                        display_summary = displaySummary,
                        packages,
                        filters = filterList,
                        update_approval = rule.AutoApprove ? "auto" : "manual",
                        automatic_approval_delay_days = rule.DeferDays,
                        scope,
                        reboot_options = new
                        {
                            auto_reboot = rule.AllowReboot ? "yes" : "no",
                            show_message = "yes",
                            message_text = "System maintenance is in progress. Please save your work.",
                            timeout = 240
                        }
                    }
                }
            }
        };
    }
}
