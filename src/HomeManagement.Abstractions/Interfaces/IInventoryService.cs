using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Maintains the registry of managed machines with their metadata and group membership.
/// </summary>
public interface IInventoryService
{
    Task<Machine> AddAsync(MachineCreateRequest request, CancellationToken ct = default);
    Task<Machine> UpdateAsync(Guid id, MachineUpdateRequest request, CancellationToken ct = default);

    /// <summary>Soft-delete a machine. Audit events and patch history are preserved.</summary>
    Task RemoveAsync(Guid id, CancellationToken ct = default);

    Task<Machine?> GetAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default);
    Task<Machine> RefreshMetadataAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Machine>> DiscoverAsync(CidrRange range, CancellationToken ct = default);
    Task ImportAsync(Stream csvStream, CancellationToken ct = default);
    Task ExportAsync(MachineQuery query, Stream destination, ExportFormat format, CancellationToken ct = default);
}
