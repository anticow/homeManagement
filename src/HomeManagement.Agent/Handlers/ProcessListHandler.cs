using System.ComponentModel;
using System.Text.Json;
using HomeManagement.Agent.Protocol;
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

        var processes = new List<ProcessSnapshot>();
        var snapshot = System.Diagnostics.Process.GetProcesses();
        try
        {
            foreach (var process in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    processes.Add(new ProcessSnapshot(
                        ProcessId: process.Id,
                        ProcessName: process.ProcessName,
                        WorkingSetBytes: process.WorkingSet64,
                        Status: "Running"));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    logger.LogDebug(ex, "Unable to read process {ProcessId}", process.Id);
                }
            }
        }
        finally
        {
            foreach (var process in snapshot)
                process.Dispose();
        }
        processes.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.ProcessName, right.ProcessName));

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
