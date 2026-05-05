using System.Text.Json;
using HomeManagement.Agent.Protocol;
using HomeManagement.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Handlers;

/// <summary>
/// Handles "ProcessList" commands from the controller.
/// Returns a JSON array of running processes on the local (agent) machine.
/// CPU percentage is omitted — accurate CPU sampling requires two measurements
/// separated by an interval and is too expensive for a one-shot snapshot.
/// </summary>
public sealed class ProcessListHandler(ILogger<ProcessListHandler> logger) : ICommandHandler
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string CommandType => "ProcessList";

    public Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken ct)
    {
        logger.LogInformation("ProcessList requested for {RequestId}", request.RequestId);

        var processes = System.Diagnostics.Process.GetProcesses()
            .Select(p =>
            {
                try
                {
                    return new ProcessSnapshot(
                        ProcessId: p.Id,
                        ProcessName: p.ProcessName,
                        WorkingSetBytes: p.WorkingSet64,
                        Status: "Running");
                }
                catch
                {
                    // Access denied or process exited between enumeration and field read
                    return null;
                }
            })
            .Where(p => p is not null)
            .OrderBy(p => p!.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogDebug("ProcessList returning {Count} processes for {RequestId}",
            processes.Count, request.RequestId);

        return Task.FromResult(new CommandResponse
        {
            RequestId = request.RequestId,
            ExitCode = 0,
            ResultJson = JsonSerializer.Serialize(processes, SerializerOptions)
        });
    }

    private sealed record ProcessSnapshot(int ProcessId, string ProcessName, long WorkingSetBytes, string Status);
}
