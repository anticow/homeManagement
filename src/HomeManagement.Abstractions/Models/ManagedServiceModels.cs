using System.Text.Json.Serialization;

namespace HomeManagement.Abstractions.Models;

public sealed record ManagedServiceEnrollment(string Id, string DisplayName, string ServiceName);

public sealed record ManagedServiceManifest(int SchemaVersion, IReadOnlyList<ManagedServiceEnrollment> Services);

public sealed record ManagedServiceSnapshot(
    string Id, string DisplayName, string ServiceName, ServiceState State, DateTime ObservedUtc);

public enum ManagedServiceAction { Start, Stop }

public sealed record ManagedServiceRequest(
    [property: JsonRequired] string Id,
    [property: JsonRequired] ManagedServiceAction Action);
