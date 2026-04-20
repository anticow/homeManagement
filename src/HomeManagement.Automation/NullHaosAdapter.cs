using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.Automation;

internal sealed class NullHaosAdapter : IHaosAdapter
{
    public Task<HaosSupervisorStatus> GetSupervisorStatusAsync(string? instanceName = null, CancellationToken ct = default)
    {
        var status = new HaosSupervisorStatus(
            InstanceName: string.IsNullOrWhiteSpace(instanceName) ? "haos-default" : instanceName,
            Version: "unknown",
            Health: "Unknown",
            RetrievedUtc: DateTime.UtcNow,
            Metadata: new Dictionary<string, string>
            {
                ["source"] = "null-adapter",
                ["note"] = "No HAOS adapter configured"
            });

        return Task.FromResult(status);
    }

    public Task<IReadOnlyList<HaosEntityState>> GetEntitiesAsync(string? domainFilter = null, int maxEntities = 250, string? instanceName = null, CancellationToken ct = default)
    {
        IReadOnlyList<HaosEntityState> entities = [];
        return Task.FromResult(entities);
    }
}
