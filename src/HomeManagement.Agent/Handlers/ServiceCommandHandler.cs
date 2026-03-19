using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Handlers;

/// <summary>
/// Handles service start/stop/restart/status commands by delegating to
/// systemctl (Linux) or sc.exe / PowerShell (Windows).
/// </summary>
public sealed class ServiceCommandHandler(ILogger<ServiceCommandHandler> logger) : ICommandHandler
{
    // Strict validation: alphanumeric, hyphens, dots, underscores only
    private static readonly System.Text.RegularExpressions.Regex SafeServiceNamePattern =
        new(@"^[\w.\-]+$", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public string CommandType => "ServiceControl";

    public async Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken ct)
    {
        var parameters = JsonSerializer.Deserialize<ServiceControlParameters>(request.ParametersJson)
            ?? throw new InvalidOperationException("Missing ServiceControl parameters.");

        if (!SafeServiceNamePattern.IsMatch(parameters.ServiceName))
        {
            return new CommandResponse
            {
                RequestId = request.RequestId,
                ExitCode = -1,
                Stderr = "Invalid service name.",
                ErrorCategory = "Authorization"
            };
        }

        logger.LogInformation("ServiceControl: {Action} {Service} for {RequestId}",
            parameters.Action, parameters.ServiceName, request.RequestId);

        var (exitCode, stdout, stderr) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? await ExecuteWindowsServiceCommandAsync(parameters, ct)
            : await ExecuteLinuxServiceCommandAsync(parameters, ct);

        return new CommandResponse
        {
            RequestId = request.RequestId,
            ExitCode = exitCode,
            Stdout = stdout,
            Stderr = stderr,
            ResultJson = JsonSerializer.Serialize(new { parameters.ServiceName, parameters.Action, Success = exitCode == 0 })
        };
    }

    private static ProcessStartInfo BuildLinuxServicePsi(ServiceControlParameters p)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/systemctl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var action = p.Action.ToLowerInvariant() switch
        {
            "start" or "stop" or "restart" or "enable" or "disable" => p.Action.ToLowerInvariant(),
            "status" => "show",
            _ => throw new ArgumentException($"Unsupported action: {p.Action}")
        };

        psi.ArgumentList.Add(action);
        psi.ArgumentList.Add(p.ServiceName);

        if (action == "show")
            psi.ArgumentList.Add("--property=ActiveState,SubState,MainPID");

        return psi;
    }

    private static ProcessStartInfo BuildWindowsServicePsi(ServiceControlParameters p)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        switch (p.Action.ToLowerInvariant())
        {
            case "start":
                psi.FileName = "sc.exe";
                psi.ArgumentList.Add("start");
                psi.ArgumentList.Add(p.ServiceName);
                break;
            case "stop":
                psi.FileName = "sc.exe";
                psi.ArgumentList.Add("stop");
                psi.ArgumentList.Add(p.ServiceName);
                break;
            case "restart":
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add($"Restart-Service '{p.ServiceName}'");
                break;
            case "enable":
                psi.FileName = "sc.exe";
                psi.ArgumentList.Add("config");
                psi.ArgumentList.Add(p.ServiceName);
                psi.ArgumentList.Add("start=");
                psi.ArgumentList.Add("auto");
                break;
            case "disable":
                psi.FileName = "sc.exe";
                psi.ArgumentList.Add("config");
                psi.ArgumentList.Add(p.ServiceName);
                psi.ArgumentList.Add("start=");
                psi.ArgumentList.Add("disabled");
                break;
            case "status":
                psi.FileName = "sc.exe";
                psi.ArgumentList.Add("query");
                psi.ArgumentList.Add(p.ServiceName);
                break;
            default:
                throw new ArgumentException($"Unsupported action: {p.Action}");
        }

        return psi;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteLinuxServiceCommandAsync(
        ServiceControlParameters p, CancellationToken ct)
    {
        return await RunProcessAsync(BuildLinuxServicePsi(p), ct);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteWindowsServiceCommandAsync(
        ServiceControlParameters p, CancellationToken ct)
    {
        return await RunProcessAsync(BuildWindowsServicePsi(p), ct);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record ServiceControlParameters(string ServiceName, string Action);
}
