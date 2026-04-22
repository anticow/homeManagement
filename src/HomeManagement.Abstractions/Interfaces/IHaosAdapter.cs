using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

public interface IHaosAdapter
{
    Task<HaosSupervisorStatus> GetSupervisorStatusAsync(string? instanceName = null, CancellationToken ct = default);

    Task<IReadOnlyList<HaosEntityState>> GetEntitiesAsync(
        string? domainFilter = null,
        int maxEntities = 250,
        string? instanceName = null,
        CancellationToken ct = default);
}
