using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using HomeManagement.Abstractions.Models;

namespace HomeManagement.AgentGateway.Host.Services;

internal sealed class AgentEnrollmentRegistry : IDisposable, IHealthCheck
{
    private readonly EncryptedAgentEnrollmentStore _store;
    private AgentEnrollmentDocument _document;
    private bool _faulted;
    private bool _disposed;
    private readonly string[] _privilegedKeys;

    // Lifecycle commits and session admission share this gate. Transport callbacks/awaits run outside it.
    internal object SyncRoot { get; } = new();
    public event Action? Changed;

    public AgentEnrollmentRegistry(EncryptedAgentEnrollmentStore store, params string[] privilegedKeys)
    {
        _store = store;
        _privilegedKeys = privilegedKeys.ToArray();
        try
        {
            if (_privilegedKeys.Any(key => !IsStrongKey(key) || store.UsesWrappingKey(key))
                || _privilegedKeys.SelectMany((left, index) =>
                    _privilegedKeys.Skip(index + 1).Select(right => Matches(left, right))).Any(equal => equal))
                throw new InvalidDataException("Agent enrollment privileged keys must be strong and distinct.");
            _document = store.Load();
            ValidateKeySeparation(_document);
        }
        catch { store.Dispose(); throw; }
    }

    public bool IsHealthy { get { lock (SyncRoot) return !_faulted && !_disposed; } }
    internal Guid RegistryId { get { lock (SyncRoot) { RequireHealthy(); return _document.RegistryId; } } }

    public IReadOnlyList<AgentEnrollmentMetadata> List()
    {
        lock (SyncRoot)
        {
            RequireHealthy();
            return _document.Entries.Select(e => e.Metadata).ToArray();
        }
    }

    public AgentEnrollmentMetadata ForMachine(Guid machineId)
    {
        lock (SyncRoot)
        {
            RequireHealthy();
            return _document.Entries.SingleOrDefault(e => e.Metadata.MachineId == machineId)?.Metadata
                ?? throw new KeyNotFoundException("The machine has no explicit agent enrollment binding.");
        }
    }

    public AgentEnrollmentEntry Ensure(string agentId, string inventoryHostname, string expectedWireHostname)
    {
        lock (SyncRoot)
        {
            RequireHealthy();
            var entry = Find(agentId);
            if (entry.Metadata.State == AgentEnrollmentState.Revoked
                || entry.Metadata.InventoryHostname != AgentEnrollmentIdentity.Hostname(inventoryHostname)
                || entry.Metadata.ExpectedWireHostname != AgentEnrollmentIdentity.Hostname(expectedWireHostname))
                throw new InvalidOperationException("Enrollment is revoked or its explicit binding does not match.");
            // Pending is explicitly authorized for provisioning/admission; handshake never enables it.
            return entry;
        }
    }

    public long Authenticate(string agentId, string wireHostname, string? credential, bool encrypted)
    {
        lock (SyncRoot)
        {
            RequireHealthy();
            var entry = Find(agentId);
            if (!encrypted || entry.Metadata.State == AgentEnrollmentState.Revoked
                || entry.Metadata.ExpectedWireHostname != AgentEnrollmentIdentity.Hostname(wireHostname)
                || !Matches(entry.Credential, credential))
                throw new InvalidOperationException("Agent enrollment authentication rejected.");
            return entry.Metadata.CredentialGeneration;
        }
    }

    public bool IsCurrent(string agentId, long generation)
    {
        lock (SyncRoot)
        {
            return !_faulted && !_disposed && _document.Entries.Any(e => e.Metadata.AgentId == agentId
                && e.Metadata.CredentialGeneration == generation && e.Metadata.State != AgentEnrollmentState.Revoked);
        }
    }

    public AgentEnrollmentMetadata Assign(AgentEnrollmentAssignment assignment) =>
        Mutate(document =>
        {
            var entry = CreateEntry(assignment);
            return document with { Entries = document.Entries.Append(entry).ToArray() };
        }, AgentEnrollmentIdentity.AgentId(assignment.AgentId));

    public AgentEnrollmentMetadata Transition(string agentId, long expectedVersion, AgentEnrollmentTransition transition)
    {
        var id = AgentEnrollmentIdentity.AgentId(agentId);
        return Mutate(document =>
        {
            var current = Find(id);
            RequireVersion(current, expectedVersion);
            var revoked = current.Metadata.State == AgentEnrollmentState.Revoked;
            if (!Enum.IsDefined(transition) || (transition == AgentEnrollmentTransition.Reenable) != revoked)
                throw new InvalidOperationException("Illegal agent enrollment transition.");
            var state = transition == AgentEnrollmentTransition.Revoke
                ? AgentEnrollmentState.Revoked : AgentEnrollmentState.Enabled;
            var next = current with
            {
                Credential = NewKey(),
                Metadata = current.Metadata with
                {
                    State = state,
                    Version = checked(current.Metadata.Version + 1),
                    CredentialGeneration = checked(current.Metadata.CredentialGeneration + 1),
                    UpdatedUtc = DateTimeOffset.UtcNow
                }
            };
            return document with { Entries = document.Entries.Select(e => e == current ? next : e).ToArray() };
        }, id);
    }

    public AgentEnrollmentMetadata Rebind(string agentId, long expectedVersion, string inventoryHostname,
        string expectedWireHostname, Guid? machineId)
    {
        var id = AgentEnrollmentIdentity.AgentId(agentId);
        return Mutate(document =>
        {
            var current = Find(id);
            RequireVersion(current, expectedVersion);
            if (current.Metadata.State != AgentEnrollmentState.Revoked)
                throw new InvalidOperationException("Rebinding requires revoked enrollment.");
            var next = current with
            {
                Credential = NewKey(),
                Metadata = current.Metadata with
                {
                    InventoryHostname = AgentEnrollmentIdentity.Hostname(inventoryHostname),
                    ExpectedWireHostname = AgentEnrollmentIdentity.Hostname(expectedWireHostname),
                    MachineId = machineId,
                    Version = checked(current.Metadata.Version + 1),
                    CredentialGeneration = checked(current.Metadata.CredentialGeneration + 1),
                    UpdatedUtc = DateTimeOffset.UtcNow
                }
            };
            return document with { Entries = document.Entries.Select(e => e == current ? next : e).ToArray() };
        }, id);
    }

    private AgentEnrollmentMetadata Mutate(Func<AgentEnrollmentDocument, AgentEnrollmentDocument> change, string id)
    {
        Exception? persistenceFailure = null;
        AgentEnrollmentMetadata? result = null;
        lock (SyncRoot)
        {
            RequireHealthy();
            var candidate = change(_document) with { Sequence = checked(_document.Sequence + 1) };
            Validate(candidate);
            ValidateKeySeparation(candidate);
            try { _store.Commit(candidate); }
            catch (Exception ex)
            {
                _faulted = true;
                persistenceFailure = ex;
            }
            if (persistenceFailure is null)
            {
                _document = candidate;
                result = Find(id).Metadata;
            }
        }
        NotifyChanged(persistenceFailure);
        return result ?? throw new InvalidOperationException("Agent enrollment mutation did not complete.");
    }

    private void NotifyChanged(Exception? primaryFailure = null)
    {
        List<Exception> failures = [];
        if (primaryFailure is not null) failures.Add(primaryFailure);
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Agent enrollment mutation or session notification failed.", failures);
    }

    private AgentEnrollmentEntry Find(string agentId) =>
        _document.Entries.SingleOrDefault(e => e.Metadata.AgentId == AgentEnrollmentIdentity.AgentId(agentId))
            ?? throw new KeyNotFoundException("The agent is not explicitly assigned.");

    private static void RequireVersion(AgentEnrollmentEntry entry, long expected)
    {
        if (entry.Metadata.Version != expected)
            throw new InvalidOperationException("Agent enrollment version conflict.");
    }

    private void RequireHealthy()
    {
        if (_faulted || _disposed)
            throw new InvalidOperationException("Agent enrollment storage is unavailable; access is fenced.");
    }

    private void ValidateKeySeparation(AgentEnrollmentDocument document)
    {
        if (document.Entries.Any(entry => _store.UsesWrappingKey(entry.Credential)
            || _privilegedKeys.Any(key => Matches(key, entry.Credential))))
            throw new InvalidDataException("Node credentials must be distinct from privileged keys.");
    }

    internal static AgentEnrollmentDocument CreateDocument(IReadOnlyList<AgentEnrollmentAssignment> assignments)
    {
        if (assignments.Count > 4096) throw new InvalidDataException("Too many agent enrollment assignments.");
        var entries = assignments.Select(CreateEntry).ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(assignments)));
        var document = new AgentEnrollmentDocument(Guid.NewGuid(), 1, digest, entries);
        Validate(document);
        return document;
    }

    private static AgentEnrollmentEntry CreateEntry(AgentEnrollmentAssignment assignment) => new(
        new AgentEnrollmentMetadata(AgentEnrollmentIdentity.AgentId(assignment.AgentId),
            AgentEnrollmentIdentity.Hostname(assignment.InventoryHostname),
            AgentEnrollmentIdentity.Hostname(assignment.ExpectedWireHostname), assignment.MachineId,
            assignment.Revoked ? AgentEnrollmentState.Revoked : AgentEnrollmentState.Pending,
            1, 1, DateTimeOffset.UtcNow), NewKey());

    internal static void Validate(AgentEnrollmentDocument document)
    {
        if (document.RegistryId == Guid.Empty || document.Sequence < 1 || document.Entries is null
            || document.Entries.Count > 4096 || document.AssignmentDigest is not { Length: 64 }
            || !document.AssignmentDigest.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid agent enrollment document.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        var machines = new HashSet<Guid>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            if (entry?.Metadata is not { } m || m.AgentId != AgentEnrollmentIdentity.AgentId(m.AgentId)
                || m.InventoryHostname != AgentEnrollmentIdentity.Hostname(m.InventoryHostname)
                || m.ExpectedWireHostname != AgentEnrollmentIdentity.Hostname(m.ExpectedWireHostname)
                || !Enum.IsDefined(m.State) || m.Version < 1 || m.CredentialGeneration < 1
                || m.CredentialGeneration > m.Version || m.Version > document.Sequence
                || m.UpdatedUtc == default || !IsStrongKey(entry.Credential)
                || !ids.Add(m.AgentId) || !hosts.Add(m.InventoryHostname) || !keys.Add(entry.Credential)
                || (m.MachineId is { } machine && (machine == Guid.Empty || !machines.Add(machine))))
                throw new InvalidDataException("Invalid or duplicate agent enrollment binding.");
        }
    }

    internal static bool IsStrongKey(string? value) =>
        value is { Length: 44 } && AgentGatewayHostOptions.HasValidControlPlaneApiKey(new() { ApiKey = value });

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static bool Matches(string expected, string? supplied)
    {
        if (!IsStrongKey(supplied)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied!));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(IsHealthy ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Agent enrollment storage is fenced."));

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            _store.Dispose();
        }
        NotifyChanged();
    }
}
