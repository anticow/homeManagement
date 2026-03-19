using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Abstractions.Models;

// ── Services ──

public record ServiceInfo(
    ServiceName Name,
    string DisplayName,
    ServiceState State,
    ServiceStartupType StartupType,
    int? ProcessId,
    TimeSpan? Uptime,
    string[] Dependencies);

public record ServiceFilter(
    string? NamePattern = null,
    ServiceState? State = null,
    ServiceStartupType? StartupType = null);

public record ServiceActionResult(
    Guid MachineId,
    ServiceName ServiceName,
    ServiceAction Action,
    bool Success,
    ServiceState ResultingState,
    string? ErrorMessage,
    TimeSpan Duration);

public record ServiceSnapshot(
    Guid Id,
    Guid MachineId,
    string ServiceName,
    string DisplayName,
    ServiceState State,
    ServiceStartupType StartupType,
    int? ProcessId,
    DateTime CapturedUtc);
