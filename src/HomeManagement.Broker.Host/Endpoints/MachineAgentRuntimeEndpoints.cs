using System.Security.Claims;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Auth;

namespace HomeManagement.Broker.Host.Endpoints;

public static partial class MachineAgentRuntimeEndpoints
{
    public static void MapMachineAgentRuntimeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/machines/{machineId:guid}/agent-runtime/observation", async (
            Guid machineId, HttpContext context, IInventoryService inventory, IMachineAgentRuntime runtime, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps) return Results.Problem("Live node observations require HTTPS.", statusCode: 403);
            if (!RbacService.HasPermission(context.User.FindAll(ClaimTypes.Role).Select(c => c.Value), Permissions.MachinesRead))
                return Results.Forbid();
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null || machine.IsDeleted) return Results.NotFound();
            if (machine.Protocol != TransportProtocol.Agent) return Results.BadRequest();
            try { return Results.Ok(await runtime.GetMachineObservationAsync(machineId, ct)); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                LogUnavailable(context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MachineAgentRuntime"), machineId);
                return Results.Problem("Gateway live observation is unavailable. Historical inventory state is not current connectivity.",
                    statusCode: 503);
            }
        }).RequireAuthorization();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gateway live observation unavailable for {MachineId}.")]
    private static partial void LogUnavailable(ILogger logger, Guid machineId);
}
