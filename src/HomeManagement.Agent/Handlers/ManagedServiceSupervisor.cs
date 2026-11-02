using System.Diagnostics;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Agent.Handlers;

public interface IManagedServiceSupervisor
{
    Task<ServiceState> GetStateAsync(string serviceName, CancellationToken ct);
    Task ControlAsync(string serviceName, ManagedServiceAction action, CancellationToken ct);
}

public sealed class ManagedServiceSupervisor : IManagedServiceSupervisor
{
    public async Task<ServiceState> GetStateAsync(string serviceName, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            using var service = new System.ServiceProcess.ServiceController(serviceName);
            return service.Status switch
            {
                System.ServiceProcess.ServiceControllerStatus.Running => ServiceState.Running,
                System.ServiceProcess.ServiceControllerStatus.Stopped => ServiceState.Stopped,
                System.ServiceProcess.ServiceControllerStatus.StartPending => ServiceState.Starting,
                System.ServiceProcess.ServiceControllerStatus.StopPending => ServiceState.Stopping,
                System.ServiceProcess.ServiceControllerStatus.Paused => ServiceState.Paused,
                _ => ServiceState.Unknown
            };
        }
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Managed services support Windows SCM and Linux systemd.");

        var output = await SystemctlAsync(serviceName, "show", ct);
        if (output.Contains("LoadState=not-found", StringComparison.Ordinal))
            throw new InvalidOperationException($"Enrolled service '{serviceName}' is not installed.");
        return output.Split('\n').Select(line => line.Trim()).FirstOrDefault(
            line => line.StartsWith("ActiveState=", StringComparison.Ordinal)) switch
        {
            "ActiveState=active" => ServiceState.Running,
            "ActiveState=inactive" => ServiceState.Stopped,
            "ActiveState=activating" => ServiceState.Starting,
            "ActiveState=deactivating" => ServiceState.Stopping,
            "ActiveState=failed" => ServiceState.Failed,
            _ => throw new InvalidOperationException("Invalid systemd service state response.")
        };
    }

    public async Task ControlAsync(string serviceName, ManagedServiceAction action, CancellationToken ct)
    {
        if (!Enum.IsDefined(action))
            throw new ArgumentOutOfRangeException(nameof(action));
        ct.ThrowIfCancellationRequested();
        var desired = action == ManagedServiceAction.Start ? ServiceState.Running : ServiceState.Stopped;
        if (await GetStateAsync(serviceName, ct) == desired)
            return;
        if (OperatingSystem.IsWindows())
        {
            using var service = new System.ServiceProcess.ServiceController(serviceName);
            if (action == ManagedServiceAction.Start)
                service.Start();
            else
                service.Stop();
        }
        else
        {
            await SystemctlAsync(serviceName, action == ManagedServiceAction.Start ? "start" : "stop", ct);
        }
        while (await GetStateAsync(serviceName, ct) != desired)
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
    }

    private static async Task<string> SystemctlAsync(string serviceName, string action, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/usr/bin/systemctl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(action);
        psi.ArgumentList.Add("--no-ask-password");
        if (action == "show")
            psi.ArgumentList.Add("--property=LoadState,ActiveState");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(serviceName);
        var result = await AgentProcessRunner.RunAsync(psi, ct);
        if (result.ExitCode != 0 || result.OutputTruncated)
            throw new InvalidOperationException($"systemctl {action} failed: {result.Stderr}");
        return result.Stdout;
    }
}
