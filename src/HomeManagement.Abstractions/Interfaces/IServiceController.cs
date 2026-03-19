using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Abstractions.Interfaces;

/// <summary>
/// Manages system services (daemons) on remote machines — start, stop, restart, query status.
/// </summary>
public interface IServiceController
{
    Task<ServiceInfo> GetStatusAsync(MachineTarget target, ServiceName serviceName, CancellationToken ct = default);

    /// <summary>List services matching a filter. If filter is null, returns running services only (not all).</summary>
    Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default);

    /// <summary>Stream service listing for machines with thousands of services.</summary>
    IAsyncEnumerable<ServiceInfo> ListServicesStreamAsync(MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default);

    Task<ServiceActionResult> ControlAsync(MachineTarget target, ServiceName serviceName, ServiceAction action, CancellationToken ct = default);
    Task<IReadOnlyList<ServiceActionResult>> BulkControlAsync(IReadOnlyList<MachineTarget> targets, ServiceName serviceName, ServiceAction action, CancellationToken ct = default);
}
