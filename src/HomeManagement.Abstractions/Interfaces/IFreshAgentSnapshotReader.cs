using HomeManagement.Abstractions.Models;

namespace HomeManagement.Abstractions.Interfaces;

public interface IFreshAgentSnapshotReader
{
    /// <summary>
    /// Performs a bounded, fresh legacy gateway read. Throws on failure or managed mode;
    /// never returns a cached fallback.
    /// </summary>
    Task<IReadOnlyList<ConnectedAgent>> ReadFreshLegacySnapshotAsync(CancellationToken ct = default);
}
