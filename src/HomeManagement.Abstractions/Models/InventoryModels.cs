using System.Net;
using HomeManagement.Abstractions.Validation;

namespace HomeManagement.Abstractions.Models;

// ── Machine & Inventory ──

public record Machine(
    Guid Id,
    Hostname Hostname,
    string? Fqdn,
    IPAddress[] IpAddresses,
    OsType OsType,
    string OsVersion,
    MachineConnectionMode ConnectionMode,
    TransportProtocol Protocol,
    int Port,
    Guid CredentialId,
    MachineState State,
    IReadOnlyDictionary<string, string> Tags,
    HardwareInfo? Hardware,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime LastContactUtc,
    bool IsDeleted = false);

public record HardwareInfo(
    int CpuCores,
    long RamBytes,
    DiskInfo[] Disks,
    string Architecture);

public record DiskInfo(
    string MountPoint,
    long TotalBytes,
    long FreeBytes);

public record MachineTarget(
    Guid MachineId,
    Hostname Hostname,
    OsType OsType,
    MachineConnectionMode ConnectionMode,
    TransportProtocol Protocol,
    int Port,
    Guid CredentialId);

public record MachineCreateRequest(
    Hostname Hostname,
    string? Fqdn,
    OsType OsType,
    MachineConnectionMode ConnectionMode,
    TransportProtocol Protocol,
    int Port,
    Guid CredentialId,
    Dictionary<string, string>? Tags = null);

public record MachineUpdateRequest(
    Hostname? Hostname = null,
    string? Fqdn = null,
    MachineConnectionMode? ConnectionMode = null,
    TransportProtocol? Protocol = null,
    int? Port = null,
    Guid? CredentialId = null,
    MachineState? State = null,
    Dictionary<string, string>? Tags = null);

public record MachineQuery(
    string? SearchText = null,
    OsType? OsType = null,
    MachineState? State = null,
    MachineConnectionMode? ConnectionMode = null,
    string? Tag = null,
    bool IncludeDeleted = false,
    int Page = 1,
    int PageSize = 50);
