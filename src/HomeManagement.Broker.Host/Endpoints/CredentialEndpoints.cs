using HomeManagement.Abstractions.Interfaces;

namespace HomeManagement.Broker.Host.Endpoints;

/// <summary>
/// Credential management endpoints (metadata only — secrets never leave the broker).
/// </summary>
public static class CredentialEndpoints
{
    public static void MapCredentialEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/credentials")
            .WithTags("Credentials")
            .RequireAuthorization();

        group.MapGet("/", async (ICredentialVault vault, CancellationToken ct) =>
        {
            var entries = await vault.ListAsync(ct);
            return Results.Ok(entries);
        });

        group.MapDelete("/{id:guid}", async (Guid id, ICredentialVault vault, CancellationToken ct) =>
        {
            await vault.RemoveAsync(id, ct);
            return Results.NoContent();
        });
    }
}
