using System.Text.Json;
using HomeManagement.Abstractions.Models;
using HomeManagement.Agent.Protocol;
using Microsoft.Extensions.Logging;

namespace HomeManagement.Agent.Handlers;

public sealed class ManagedServicesHandler(
    ManagedServiceRegistry registry,
    IManagedServiceSupervisor supervisor,
    ILogger<ManagedServicesHandler> logger) : ICommandHandler, IDisposable
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public string CommandType => "ManagedServices";

    public async Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken ct)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            return await ExecuteAsync(request, ct);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<CommandResponse> ExecuteAsync(CommandRequest request, CancellationToken ct)
    {
        var enrollments = registry.Read();
        if (!string.IsNullOrEmpty(request.CommandText))
        {
            var control = JsonSerializer.Deserialize<ManagedServiceRequest>(request.CommandText, JsonOptions)
                ?? throw new InvalidOperationException("Missing managed service control request.");
            if (!Enum.IsDefined(control.Action))
                throw new InvalidOperationException("Only Start and Stop are supported.");
            var enrollment = enrollments.SingleOrDefault(
                service => string.Equals(service.Id, control.Id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Service is not enrolled on this node.");
            logger.LogInformation("Managed service {Id}: {Action}, request {RequestId}",
                enrollment.Id, control.Action, request.RequestId);
            ct.ThrowIfCancellationRequested();
            await supervisor.ControlAsync(enrollment.ServiceName, control.Action, ct);
        }
        var snapshots = new List<ManagedServiceSnapshot>();
        foreach (var service in enrollments)
        {
            snapshots.Add(new ManagedServiceSnapshot(service.Id, service.DisplayName, service.ServiceName,
                await supervisor.GetStateAsync(service.ServiceName, ct), DateTime.UtcNow));
        }
        return new CommandResponse
        {
            RequestId = request.RequestId,
            ExitCode = 0,
            Stdout = JsonSerializer.Serialize(snapshots, JsonOptions)
        };
    }

    public void Dispose() => _operationLock.Dispose();
}
