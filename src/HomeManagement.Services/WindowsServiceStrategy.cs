using System.Text.Json;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Services;

/// <summary>
/// Windows service management via sc.exe / Get-Service PowerShell commands.
/// Parses JSON output from ConvertTo-Json for structured results.
/// </summary>
internal sealed class WindowsServiceStrategy : IServiceStrategy
{
    public OsType TargetOs => OsType.Windows;

    public string BuildStatusCommand(ServiceName serviceName) =>
        $"Get-Service -Name '{serviceName}' | Select-Object Name,DisplayName,Status,StartType | ConvertTo-Json -Compress";

    public ServiceInfo ParseStatusOutput(string stdout, ServiceName serviceName)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return new ServiceInfo(serviceName, serviceName.ToString(), ServiceState.Unknown,
                ServiceStartupType.Manual, null, null, []);

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var el = doc.RootElement;
            // PowerShell may return array for single object with -Compress depending on version
            if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0)
                el = el[0];

            return ParseServiceElement(el, serviceName);
        }
        catch (JsonException)
        {
            return new ServiceInfo(serviceName, serviceName.ToString(), ServiceState.Unknown,
                ServiceStartupType.Manual, null, null, []);
        }
    }

    public string BuildListCommand(ServiceFilter? filter)
    {
        var statusFilter = filter?.State switch
        {
            ServiceState.Running => " | Where-Object Status -eq 'Running'",
            ServiceState.Stopped => " | Where-Object Status -eq 'Stopped'",
            _ => ""
        };
        return $"Get-Service{statusFilter} | Select-Object Name,DisplayName,Status,StartType | ConvertTo-Json -Compress";
    }

    public IReadOnlyList<ServiceInfo> ParseListOutput(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return [];

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                var name = root.TryGetProperty("Name", out var np) ? np.GetString() ?? "" : "";
                if (!ServiceName.TryCreate(name, out var sn, out _))
                    return [];
                return [ParseServiceElement(root, sn)];
            }

            if (root.ValueKind != JsonValueKind.Array)
                return [];

            var services = new List<ServiceInfo>(root.GetArrayLength());
            foreach (var el in root.EnumerateArray())
            {
                var rawName = el.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                if (!ServiceName.TryCreate(rawName, out var serviceName, out _))
                    continue;
                services.Add(ParseServiceElement(el, serviceName));
            }
            return services;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public string BuildControlCommand(ServiceName serviceName, ServiceAction action)
    {
        return action switch
        {
            ServiceAction.Start => $"Start-Service -Name '{serviceName}'",
            ServiceAction.Stop => $"Stop-Service -Name '{serviceName}' -Force",
            ServiceAction.Restart => $"Restart-Service -Name '{serviceName}' -Force",
            ServiceAction.Enable => $"Set-Service -Name '{serviceName}' -StartupType Automatic",
            ServiceAction.Disable => $"Set-Service -Name '{serviceName}' -StartupType Disabled",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    private static ServiceInfo ParseServiceElement(JsonElement el, ServiceName serviceName)
    {
        var displayName = el.TryGetProperty("DisplayName", out var dnProp) ? dnProp.GetString() ?? serviceName.ToString() : serviceName.ToString();
        var statusStr = el.TryGetProperty("Status", out var statusProp) ? statusProp.ToString() : "";
        var startTypeStr = el.TryGetProperty("StartType", out var startProp) ? startProp.ToString() : "";

        var state = MapStatus(statusStr);
        var startupType = MapStartType(startTypeStr);

        return new ServiceInfo(serviceName, displayName, state, startupType, null, null, []);
    }

    // PowerShell Get-Service Status values: Running, Stopped, Paused, StartPending, StopPending, ContinuePending, PausePending
    // Also handles numeric enum values that ConvertTo-Json may emit (e.g. 4 = Running)
    private static ServiceState MapStatus(string status) => status switch
    {
        "Running" or "4" => ServiceState.Running,
        "Stopped" or "1" => ServiceState.Stopped,
        "Paused" or "7" => ServiceState.Paused,
        "StartPending" or "2" => ServiceState.Starting,
        "StopPending" or "3" => ServiceState.Stopping,
        _ => ServiceState.Unknown
    };

    // PowerShell StartType: Automatic, Manual, Disabled, Boot, System
    private static ServiceStartupType MapStartType(string startType) => startType switch
    {
        "Automatic" or "2" => ServiceStartupType.Automatic,
        "Manual" or "3" => ServiceStartupType.Manual,
        "Disabled" or "4" => ServiceStartupType.Disabled,
        _ => ServiceStartupType.Manual
    };
}
