using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeManagement.AgentGateway.Host.Services;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.AgentGateway.Host.Endpoints;

internal static class AgentEnrollmentEndpoints
{
    internal const string ProvisioningPath = "/internal/agent-provisioning/ensure";
    internal const string ProvisioningHeader = "x-agent-provisioning-api-key";
    internal const string EnrollmentPath = "/internal/agents/enrollments";
    internal const int MaximumRequestBytes = 8192;

    public static void UseAgentEnrollmentBoundary(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var provision = context.Request.Path.Value?.StartsWith("/internal/agent-provisioning", StringComparison.OrdinalIgnoreCase) == true;
            var enrollment = context.Request.Path.StartsWithSegments(EnrollmentPath);
            var mode = context.RequestServices.GetRequiredService<AgentEnrollmentMode>();
            var managedControl = mode.Enabled && context.Request.Path.StartsWithSegments("/internal/agents");
            if (!provision && !enrollment && !managedControl) { await next(); return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
            if (!context.Request.IsHttps)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (provision)
            {
                if (!context.Request.Path.Equals(ProvisioningPath, StringComparison.OrdinalIgnoreCase)
                    || !HttpMethods.IsPost(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                if (!mode.Enabled) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }
                var authority = context.RequestServices.GetRequiredService<AgentProvisioningAuthority>();
                if (!HasKey(context, ProvisioningHeader, authority.Key))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            await ExecuteSafely(context, next);
        });
    }

    internal static bool HasKey(HttpContext context, string header, string expected)
    {
        var values = context.Request.Headers[header];
        if (values.Count != 1 || !AgentEnrollmentRegistry.IsStrongKey(values[0])) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(values[0]!),
            Convert.FromBase64String(expected));
    }

    private static async Task ExecuteSafely(HttpContext context, Func<Task> next)
    {
        try { await next(); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or BadHttpRequestException
            or KeyNotFoundException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException
            or CryptographicException or AggregateException or OverflowException)
        {
            if (context.Response.HasStarted) throw;
            var registry = context.RequestServices.GetService<AgentEnrollmentRegistry>();
            var status = registry is { IsHealthy: false } ? 503 : ex switch
            {
                BadHttpRequestException bad => bad.StatusCode,
                ArgumentException or JsonException => 400,
                KeyNotFoundException => 404,
                InvalidOperationException or InvalidDataException => 409,
                _ => 503
            };
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AgentEnrollment")
                .LogWarning("Agent enrollment request failed with status {Status}; refresh metadata before retrying mutations.", status);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Agent enrollment operation failed. Refresh metadata before retrying any mutation."
            }, context.RequestAborted);
        }
    }

    public static void MapAgentEnrollmentEndpoints(this WebApplication app)
    {
        app.MapPost(ProvisioningPath, async (HttpContext context) =>
        {
            var request = await ReadRequest<EnsureAgentRequest>(context);
            var registry = context.RequestServices.GetRequiredService<AgentEnrollmentRegistry>();
            var entry = registry.Ensure(request.AgentId, request.InventoryHostname, request.ExpectedWireHostname);
            return Results.Ok(new AgentProvisioningResponse(entry.Metadata, entry.Credential));
        });
        var group = app.MapGroup(EnrollmentPath);
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!context.HttpContext.RequestServices.GetRequiredService<AgentEnrollmentMode>().Enabled)
                return Results.Conflict(new { error = "Managed agent enrollment is disabled." });
            return await next(context);
        });
        group.MapGet("/", (HttpContext context) => Results.Ok(Registry(context).List()));
        group.MapGet("/machines/{machineId:guid}", (Guid machineId, HttpContext context) =>
            Results.Ok(Registry(context).ForMachine(machineId)));
        group.MapPost("/", async (HttpContext context) =>
        {
            var assignment = await ReadRequest<AgentEnrollmentAssignment>(context);
            return Results.Ok(Registry(context).Assign(assignment));
        });
        group.MapPost("/{agentId}/transition", async (string agentId, HttpContext context) =>
        {
            var request = await ReadRequest<AgentTransitionRequest>(context);
            return Results.Ok(Registry(context).Transition(agentId, request.ExpectedVersion, request.Transition));
        });
        group.MapPost("/{agentId}/rebind", async (string agentId, HttpContext context) =>
        {
            var request = await ReadRequest<AgentRebindRequest>(context);
            return Results.Ok(Registry(context).Rebind(agentId, request.ExpectedVersion, request.InventoryHostname,
                request.ExpectedWireHostname, request.MachineId));
        });
    }

    private static AgentEnrollmentRegistry Registry(HttpContext context) =>
        context.RequestServices.GetRequiredService<AgentEnrollmentRegistry>();

    internal static async Task<T> ReadRequest<T>(HttpContext context)
    {
        if (context.Request.ContentLength > MaximumRequestBytes)
            throw new BadHttpRequestException("Request too large.", StatusCodes.Status413PayloadTooLarge);
        if (!context.Request.HasJsonContentType())
            throw new BadHttpRequestException("JSON required.", StatusCodes.Status415UnsupportedMediaType);
        var bytes = new byte[MaximumRequestBytes + 1];
        var length = 0;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (length < bytes.Length)
            {
                var read = await context.Request.Body.ReadAsync(bytes.AsMemory(length), budget.Token);
                if (read == 0) break;
                length += read;
            }
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            throw new BadHttpRequestException("Request body read timed out.", StatusCodes.Status408RequestTimeout);
        }
        if (length > MaximumRequestBytes)
            throw new BadHttpRequestException("Request too large.", StatusCodes.Status413PayloadTooLarge);
        return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, length), AgentEnrollmentBootstrap.JsonOptions)
            ?? throw new JsonException("A request is required.");
    }
}

internal sealed record EnsureAgentRequest(
    [property: JsonRequired] string AgentId,
    [property: JsonRequired] string InventoryHostname,
    [property: JsonRequired] string ExpectedWireHostname);
internal sealed record AgentTransitionRequest(
    [property: JsonRequired] long ExpectedVersion, [property: JsonRequired] AgentEnrollmentTransition Transition);
internal sealed record AgentRebindRequest(
    [property: JsonRequired] long ExpectedVersion, [property: JsonRequired] string InventoryHostname,
    [property: JsonRequired] string ExpectedWireHostname, [property: JsonRequired] Guid? MachineId);
internal sealed record AgentProvisioningResponse(AgentEnrollmentMetadata Metadata, string Credential)
{
    public override string ToString() => "AgentProvisioningResponse { Credential = [redacted] }";
}
