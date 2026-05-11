using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace HomeManagement.Integration.Action1;

/// <summary>
/// Broker REST endpoints exposing Action1 endpoint inventory, available patches, and
/// patch deployment management to the web client.
///
/// All routes require the caller to be authenticated.
/// When Action1 is disabled (Action1:Enabled = false), every endpoint returns 503.
/// </summary>
public static class Action1BrokerEndpoints
{
    public static IEndpointRouteBuilder MapAction1BrokerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/action1")
            .WithTags("Action1")
            .RequireAuthorization();

        // ── Endpoints ─────────────────────────────────────────────────────────

        /// <summary>List all managed endpoints in the Action1 organization.</summary>
        group.MapGet("endpoints", async (
            Action1Client client,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            var endpoints = await client.ListEndpointsAsync(ct);
            return Results.Ok(endpoints);
        });

        /// <summary>Get a single Action1 endpoint by its Action1 ID.</summary>
        group.MapGet("endpoints/{endpointId}", async (
            string endpointId,
            Action1Client client,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            var endpoint = await client.GetEndpointAsync(endpointId, ct);
            return endpoint is null ? Results.NotFound() : Results.Ok(endpoint);
        });

        // ── Patches ───────────────────────────────────────────────────────────

        /// <summary>Get available (pending) patches for an Action1 endpoint.</summary>
        group.MapGet("endpoints/{endpointId}/patches", async (
            string endpointId,
            Action1Client client,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            var patches = await client.GetAvailablePatchesAsync(endpointId, ct);
            return Results.Ok(patches);
        });

        /// <summary>Get installed software inventory for an Action1 endpoint.</summary>
        group.MapGet("endpoints/{endpointId}/software", async (
            string endpointId,
            Action1Client client,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            var software = await client.GetSoftwareInventoryAsync(endpointId, ct);
            return Results.Ok(software);
        });

        // ── Deployments ───────────────────────────────────────────────────────

        /// <summary>
        /// Create a patch deployment for selected patches on an endpoint.
        /// This is the approval gate: an authenticated user explicitly approves the
        /// patches to deploy by calling this endpoint with the desired patch IDs.
        /// </summary>
        group.MapPost("endpoints/{endpointId}/deploy", async (
            string endpointId,
            DeployPatchesRequest request,
            Action1Client client,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            if (request.PatchIds is null || request.PatchIds.Count == 0)
                return Results.BadRequest("At least one patch ID is required.");

            var deploymentId = await client.CreateDeploymentAsync(
                endpointId, request.PatchIds, request.AllowReboot, ct);

            if (deploymentId is null)
                return Results.Problem("Action1 failed to create the deployment.", statusCode: 502);

            return Results.Accepted($"/api/action1/deployments/{deploymentId}",
                new { DeploymentId = deploymentId });
        });

        /// <summary>Get the current status of a deployment.</summary>
        group.MapGet("deployments/{deploymentId}", async (
            string deploymentId,
            Action1Client client,
            IOptions<Action1Options> opts,
            CancellationToken ct) =>
        {
            if (!opts.Value.Enabled)
                return Results.Problem("Action1 integration is not enabled.", statusCode: 503);

            var deployment = await client.GetDeploymentAsync(deploymentId, ct);
            return deployment is null ? Results.NotFound() : Results.Ok(deployment);
        });

        return app;
    }
}

/// <summary>Request body for POST /api/action1/endpoints/{id}/deploy.</summary>
public sealed record DeployPatchesRequest(
    IReadOnlyList<string> PatchIds,
    bool AllowReboot = false);
