using System.Net;
using System.Security.Claims;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Auth;

namespace HomeManagement.Broker.Host.Endpoints;

public static partial class AgentEnrollmentEndpoints
{
    public static void MapAgentEnrollmentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/machines/{machineId:guid}/agent-enrollment").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!context.HttpContext.Request.IsHttps)
                return Results.Problem("Agent enrollment requires HTTPS.", statusCode: 403);
            if (!RbacService.HasPermission(context.HttpContext.User.FindAll(ClaimTypes.Role).Select(c => c.Value), Permissions.AdminSettings))
                return Results.Forbid();
            try { return await next(context); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or KeyNotFoundException
                or ArgumentException or IOException or OperationCanceledException)
            {
                if (context.HttpContext.RequestAborted.IsCancellationRequested) throw;
                var status = ex switch
                {
                    HttpRequestException { StatusCode: HttpStatusCode.NotFound } or KeyNotFoundException => 404,
                    HttpRequestException { StatusCode: HttpStatusCode.Conflict } or InvalidOperationException => 409,
                    ArgumentException => 400,
                    _ => 503
                };
                LogFailure(context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AgentEnrollment"), status);
                return Results.Problem("Agent enrollment operation failed. Refresh metadata before retrying any mutation.", statusCode: status);
            }
        });
        group.MapGet("/", async (Guid machineId, IInventoryService inventory, IAgentEnrollmentControl enrollment, CancellationToken ct) =>
        {
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null || machine.IsDeleted) return Results.NotFound();
            if (machine.Protocol != TransportProtocol.Agent) return Results.BadRequest();
            return Results.Ok(await enrollment.GetEnrollmentAsync(machineId, ct));
        });
        group.MapPost("/assign", async (Guid machineId, MachineAgentEnrollmentAssignment request, HttpContext context,
            IInventoryService inventory, IAgentEnrollmentControl enrollment, IAuditLogger audit, ICorrelationContext correlation, CancellationToken ct) =>
        {
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null || machine.IsDeleted) return Results.NotFound();
            if (machine.Protocol != TransportProtocol.Agent || !MatchesInventory(machine, request.InventoryHostname))
                return Results.BadRequest(new { error = "Assignment must match the machine's authoritative inventory hostname and Agent transport." });
            return await Mutate(machine, "Assign", context, audit, correlation,
                () => enrollment.AssignEnrollmentAsync(machineId, request, ct), ct);
        });
        group.MapPost("/transition", async (Guid machineId, AgentEnrollmentChange request, HttpContext context,
            IInventoryService inventory, IAgentEnrollmentControl enrollment, IAuditLogger audit, ICorrelationContext correlation, CancellationToken ct) =>
        {
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null || machine.IsDeleted) return Results.NotFound();
            if (machine.Protocol != TransportProtocol.Agent || request.ExpectedVersion < 1 || !Enum.IsDefined(request.Transition))
                return Results.BadRequest();
            return await Mutate(machine, $"Transition {request.Transition}; confirmed version {request.ExpectedVersion}",
                context, audit, correlation, () => enrollment.ChangeEnrollmentAsync(machineId, request, ct), ct);
        });
        group.MapPost("/rebind", async (Guid machineId, AgentEnrollmentRebind request, HttpContext context,
            IInventoryService inventory, IAgentEnrollmentControl enrollment, IAuditLogger audit, ICorrelationContext correlation, CancellationToken ct) =>
        {
            var machine = await inventory.GetAsync(machineId, ct);
            if (machine is null || machine.IsDeleted) return Results.NotFound();
            if (machine.Protocol != TransportProtocol.Agent || request.ExpectedVersion < 1 || !MatchesInventory(machine, request.InventoryHostname))
                return Results.BadRequest();
            return await Mutate(machine, $"Rebind; confirmed version {request.ExpectedVersion}", context, audit, correlation,
                () => enrollment.RebindEnrollmentAsync(machineId, request, ct), ct);
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent enrollment operation failed with status {Status}. Mutation outcome may be uncertain.")]
    private static partial void LogFailure(ILogger logger, int status);

    private static bool MatchesInventory(Machine machine, string? inventory) =>
        !string.IsNullOrWhiteSpace(inventory)
        && string.Equals((machine.Fqdn ?? machine.Hostname.Value).TrimEnd('.'), inventory.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> Mutate(Machine machine, string action, HttpContext context,
        IAuditLogger audit, ICorrelationContext correlation, Func<Task<AgentEnrollmentMetadata>> mutation, CancellationToken ct)
    {
        var intent = new AuditEvent(Guid.NewGuid(), DateTime.UtcNow, correlation.CorrelationId, AuditAction.SettingsChanged,
            context.User.Identity?.Name ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "authenticated user",
            machine.Id, machine.Hostname.Value, $"Agent enrollment: {action}", null, AuditOutcome.PartialSuccess, null);
        await audit.RecordAsync(intent, ct);
        AgentEnrollmentMetadata metadata;
        try { metadata = await mutation(); }
        catch
        {
            await audit.RecordAsync(intent with
            {
                EventId = Guid.NewGuid(),
                TimestampUtc = DateTime.UtcNow,
                Outcome = AuditOutcome.Failure,
                Detail = "Agent enrollment mutation failed or its outcome is uncertain; refresh metadata."
            }, CancellationToken.None);
            throw;
        }
        await audit.RecordAsync(intent with
        {
            EventId = Guid.NewGuid(),
            TimestampUtc = DateTime.UtcNow,
            Outcome = AuditOutcome.Success,
            Detail = $"Agent enrollment committed: {metadata.AgentId}, version {metadata.Version}."
        }, CancellationToken.None);
        return Results.Ok(metadata);
    }
}
