using System.Security.Claims;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Auth;

namespace HomeManagement.Broker.Host.Endpoints;

public static class DeviceStateEndpoints
{
    public static void MapDeviceStateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/device-state", async (HttpContext context, IDeviceStateReader reader,
            int? page, int? pageSize, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!RbacService.HasPermission(context.User.FindAll(ClaimTypes.Role).Select(c => c.Value), Permissions.MachinesRead))
                return Results.Forbid();
            if (page is < 1 || pageSize is < 1 or > 100) return Results.BadRequest();
            // This reader depends exclusively on inventory/repositories, never runtime/Prometheus.
            return Results.Ok(await reader.QueryAsync(new MachineQuery(Page: page ?? 1, PageSize: pageSize ?? 25), ct));
        }).RequireAuthorization();
    }
}
