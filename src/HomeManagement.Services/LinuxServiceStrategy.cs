using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Services;

/// <summary>
/// Linux service management via systemctl. Parses machine-readable output formats.
/// </summary>
internal sealed class LinuxServiceStrategy : IServiceStrategy
{
    public OsType TargetOs => OsType.Linux;

    public string BuildStatusCommand(ServiceName serviceName) =>
        $"systemctl show {serviceName} --property=ActiveState,SubState,MainPID,LoadState,UnitFileState,Description --no-pager";

    public ServiceInfo ParseStatusOutput(string stdout, ServiceName serviceName)
    {
        var props = ParseProperties(stdout);

        var state = MapActiveState(props.GetValueOrDefault("ActiveState", "unknown"));
        var startupType = MapUnitFileState(props.GetValueOrDefault("UnitFileState", ""));
        _ = int.TryParse(props.GetValueOrDefault("MainPID", "0"), out var pid);
        var display = props.GetValueOrDefault("Description", serviceName.ToString());

        return new ServiceInfo(serviceName, display, state, startupType, pid > 0 ? pid : null, null, []);
    }

    public string BuildListCommand(ServiceFilter? filter)
    {
        var stateFilter = filter?.State switch
        {
            ServiceState.Running => " --state=running",
            ServiceState.Stopped => " --state=dead",
            _ => ""
        };
        return $"systemctl list-units --type=service{stateFilter} --no-pager --plain --no-legend";
    }

    public IReadOnlyList<ServiceInfo> ParseListOutput(string stdout)
    {
        var services = new List<ServiceInfo>();
        if (string.IsNullOrWhiteSpace(stdout)) return services;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format: "unit.service loaded active running Description text"
            var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            var unitName = parts[0].Replace(".service", "", StringComparison.OrdinalIgnoreCase);
            if (!ServiceName.TryCreate(unitName, out var name, out _)) continue;

            var state = MapActiveState(parts[2]);
            var description = parts.Length >= 5 ? parts[4].Trim() : unitName;

            services.Add(new ServiceInfo(name, description, state, ServiceStartupType.Automatic, null, null, []));
        }

        return services;
    }

    public string BuildControlCommand(ServiceName serviceName, ServiceAction action)
    {
        var verb = action switch
        {
            ServiceAction.Start => "start",
            ServiceAction.Stop => "stop",
            ServiceAction.Restart => "restart",
            ServiceAction.Enable => "enable",
            ServiceAction.Disable => "disable",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        return $"systemctl {verb} {serviceName}";
    }

    private static Dictionary<string, string> ParseProperties(string stdout)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf('=');
            if (idx > 0)
                props[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return props;
    }

    private static ServiceState MapActiveState(string activeState) => activeState.ToLowerInvariant() switch
    {
        "active" => ServiceState.Running,
        "inactive" or "dead" => ServiceState.Stopped,
        "activating" => ServiceState.Starting,
        "deactivating" => ServiceState.Stopping,
        _ => ServiceState.Unknown
    };

    private static ServiceStartupType MapUnitFileState(string unitFileState) => unitFileState.ToLowerInvariant() switch
    {
        "enabled" => ServiceStartupType.Automatic,
        "disabled" => ServiceStartupType.Disabled,
        _ => ServiceStartupType.Manual
    };
}
