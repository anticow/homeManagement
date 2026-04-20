namespace HomeManagement.Abstractions.Models;

public sealed record HaosSupervisorStatus(
    string InstanceName,
    string Version,
    string Health,
    DateTime RetrievedUtc,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record HaosEntityState(
    string EntityId,
    string State,
    DateTime LastUpdatedUtc,
    IReadOnlyDictionary<string, string>? Attributes = null);
