using System.Runtime.InteropServices;
using System.Text.Json;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Handlers;

/// <summary>
/// Collects hardware and OS metadata from the local machine.
/// Returns structured JSON matching the <c>AgentMetadata</c> schema.
/// </summary>
public sealed class SystemInfoHandler(ILogger<SystemInfoHandler> logger) : ICommandHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public string CommandType => "SystemInfo";

    public Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken ct)
    {
        logger.LogInformation("Collecting system info for {RequestId}", request.RequestId);

        var info = new Dictionary<string, object?>
        {
            ["hostname"] = Environment.MachineName,
            ["osType"] = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux",
            ["osVersion"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["processorCount"] = Environment.ProcessorCount,
            ["totalMemoryBytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
        };

        // Disk info
        var disks = new List<Dictionary<string, object>>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            disks.Add(new Dictionary<string, object>
            {
                ["mountPoint"] = drive.Name,
                ["totalBytes"] = drive.TotalSize,
                ["freeBytes"] = drive.AvailableFreeSpace
            });
        }
        info["disks"] = disks;

        var json = JsonSerializer.Serialize(info, SerializerOptions);

        return Task.FromResult(new CommandResponse
        {
            RequestId = request.RequestId,
            ExitCode = 0,
            ResultJson = json
        });
    }
}
