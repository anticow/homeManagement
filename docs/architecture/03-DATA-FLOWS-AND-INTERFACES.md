# Data Flow Diagrams & Interface Definitions

## 1. Patch Detection Flow

```
┌──────────┐     ┌──────────────┐     ┌─────────────┐     ┌─────────────┐
│   GUI    │     │ Orchestrator │     │   Patch     │     │  Transport  │
│          │     │              │     │   Module    │     │   Layer     │
└────┬─────┘     └──────┬───────┘     └──────┬──────┘     └──────┬──────┘
     │                  │                    │                    │
     │  1. User clicks  │                    │                    │
     │  "Scan for       │                    │                    │
     │   patches"       │                    │                    │
     │─────────────────→│                    │                    │
     │                  │ 2. Create Job      │                    │
     │                  │ (PatchScan type)   │                    │
     │                  │───────────────────→│                    │
     │                  │                    │                    │
     │                  │                    │ 3. For each target │
     │                  │                    │    machine:        │
     │                  │                    │                    │
     │                  │                    │ 3a. Resolve cred   │
     │                  │                    │     from Vault     │
     │                  │                    │    ┌───────────┐   │
     │                  │                    │───→│  Vault    │   │
     │                  │                    │←───│           │   │
     │                  │                    │    └───────────┘   │
     │                  │                    │                    │
     │                  │                    │ 3b. Build OS-      │
     │                  │                    │     specific       │
     │                  │                    │     detect command │
     │                  │                    │───────────────────→│
     │                  │                    │                    │
     │                  │                    │                    │ 3c. SSH/WinRM
     │                  │                    │                    │     to target
     │                  │                    │                    │────→ [Machine]
     │                  │                    │                    │←────
     │                  │                    │                    │
     │                  │                    │←───────────────────│
     │                  │                    │ 3d. Parse stdout   │
     │                  │                    │     into PatchInfo │
     │                  │                    │     objects        │
     │                  │                    │                    │
     │                  │ 4. Results +       │                    │
     │                  │    audit event     │                    │
     │                  │←───────────────────│                    │
     │                  │                    │                    │
     │  5. Update UI    │                    │                    │
     │←─────────────────│                    │                    │
     │                  │                    │                    │
```

---

## 2. Patch Application Flow

```
┌──────┐   ┌────────────┐   ┌─────────┐   ┌──────────┐   ┌───────────┐   ┌────────┐
│ GUI  │   │Orchestrator│   │  Patch  │   │Transport │   │  Audit    │   │  DB    │
│      │   │            │   │  Module │   │  Layer   │   │  Logger   │   │        │
└──┬───┘   └─────┬──────┘   └────┬────┘   └────┬─────┘   └─────┬─────┘   └───┬────┘
   │             │               │              │               │             │
   │ 1. Approve  │               │              │               │             │
   │    patches  │               │              │               │             │
   │────────────→│               │              │               │             │
   │             │               │              │               │             │
   │             │ 2. Create Job │              │               │             │
   │             │ (PatchApply)  │              │               │             │
   │             │──────────────→│              │               │             │
   │             │               │              │               │             │
   │             │               │ 3. Pre-check │              │             │
   │             │               │    disk space│              │             │
   │             │               │─────────────→│──→[Machine]  │             │
   │             │               │←─────────────│←──           │             │
   │             │               │              │               │             │
   │             │               │ 4. Install   │              │             │
   │             │               │    command   │              │             │
   │             │               │─────────────→│──→[Machine]  │             │
   │             │               │←─────────────│←──           │             │
   │             │               │              │               │             │
   │             │               │ 5. Verify    │              │             │
   │             │               │    install   │              │             │
   │             │               │─────────────→│──→[Machine]  │             │
   │             │               │←─────────────│←──           │             │
   │             │               │              │               │             │
   │             │               │ 6. Log audit │              │             │
   │             │               │──────────────┼──────────────→│             │
   │             │               │              │               │────────────→│
   │             │               │              │               │             │
   │             │               │ 7. Handle    │              │             │
   │             │               │    reboot    │              │             │
   │             │               │    (if ok)   │              │             │
   │             │               │─────────────→│──→[Machine]  │             │
   │             │               │              │               │             │
   │             │ 8. Result     │              │               │             │
   │             │←──────────────│              │               │             │
   │             │               │              │               │             │
   │ 9. Update   │              │              │               │             │
   │    display  │              │              │               │             │
   │←────────────│              │              │               │             │
```

---

## 3. Service Control Flow

```
┌──────┐   ┌────────────┐   ┌──────────┐   ┌──────────┐   ┌─────────┐
│ GUI  │   │  Service   │   │Transport │   │  Audit   │   │  Target │
│      │   │ Controller │   │  Layer   │   │  Logger  │   │ Machine │
└──┬───┘   └─────┬──────┘   └────┬─────┘   └────┬─────┘   └────┬────┘
   │             │               │              │              │
   │ 1. Restart  │               │              │              │
   │    "nginx"  │               │              │              │
   │────────────→│               │              │              │
   │             │               │              │              │
   │             │ 2. Resolve OS │              │              │
   │             │    strategy   │              │              │
   │             │    (systemd)  │              │              │
   │             │               │              │              │
   │             │ 3. Execute:   │              │              │
   │             │ systemctl     │              │              │
   │             │ restart nginx │              │              │
   │             │──────────────→│─────────────────────────────→│
   │             │               │              │              │
   │             │               │←─────────────────────────────│
   │             │←──────────────│              │              │
   │             │               │              │              │
   │             │ 4. Verify:    │              │              │
   │             │ systemctl     │              │              │
   │             │ is-active     │              │              │
   │             │──────────────→│─────────────────────────────→│
   │             │←──────────────│←─────────────────────────────│
   │             │               │              │              │
   │             │ 5. Audit log  │              │              │
   │             │──────────────────────────────→│              │
   │             │               │              │              │
   │ 6. Updated  │               │              │              │
   │    status   │               │              │              │
   │←────────────│               │              │              │
```

---

## 4. Agent Communication Flow

```
┌─────────────────────────────────────────┐     ┌──────────────────────────────────┐
│         CONTROL MACHINE                  │     │        REMOTE MACHINE            │
│                                          │     │                                  │
│  ┌──────────┐   ┌───────────────┐        │     │  ┌──────────────────────────────┐│
│  │ Command  │   │ Agent Gateway │        │     │  │    HM Agent                  ││
│  │ Broker   │──→│ (gRPC Server) │◄───────┼─────┼──│  (gRPC Client)              ││
│  │ (queue)  │←──│               │────────┼─────┼──│                              ││
│  └──────────┘   └───────────────┘        │     │  │  ┌────────────────────────┐  ││
│       ▲                                  │     │  │  │ Inbound Command Queue  │  ││
│       │                                  │     │  │  │ Channel<CommandRequest> │  ││
│  ┌──────────┐                            │     │  │  └──────────┬─────────────┘  ││
│  │ Core     │   Fire-and-forget submit   │     │  │             ▼                ││
│  │ Engine / │   with CompletedStream     │     │  │  ┌────────────────────────┐  ││
│  │ GUI VMs  │   for reactive updates     │     │  │  │ Command Dispatcher     │  ││
│  └──────────┘                            │     │  │  │ (SemaphoreSlim-bounded)│  ││
│                                          │     │  │  └────────────────────────┘  ││
│  Agent initiates outbound                │     │  └──────────────────────────────┘│
│  connection (firewall-friendly)          │     │                                  │
└─────────────────────────────────────────┘     └──────────────────────────────────┘

Message Flow (bidirectional gRPC stream):

Control ──→ Agent:  CommandRequest { id, command_text, timeout, elevated }
  Agent receive loop writes to inbound Channel<CommandRequest> (non-blocking).
  Separate CommandProcessingLoopAsync drains queue through SemaphoreSlim-bounded dispatch.
  This decouples reception from execution so update directives and shutdowns are never blocked.

Agent   ──→ Control: CommandResponse { id, exit_code, stdout, stderr, duration }

Control ──→ Agent:  MetadataRequest { }
Agent   ──→ Control: MetadataResponse { os, cpu, ram, disk, services, ... }

Control ──→ Agent:  HeartbeatPing { timestamp }
Agent   ──→ Control: HeartbeatPong { timestamp, agent_version, uptime }

Control ──→ Agent:  UpdateRequest { binary_url, sha256, version }
Agent   ──→ Control: UpdateResponse { status, new_version }

Async Command Pipeline (control-side):
  GUI/Orchestrator ──→ ICommandBroker.SubmitAsync(envelope) ──→ Channel<QueuedCommand>
    Background loop drains queue ──→ IRemoteExecutor.ExecuteAsync ──→ IJobRepository (persist)
    ──→ CompletedStream (IObservable<CommandCompletedEvent>) ──→ GUI reactive update
```

---

## 5. Complete Interface Registry

All interfaces live in `HomeManagement.Abstractions`. Below is the full surface area.

### 5.1 ICredentialVault
```csharp
public interface ICredentialVault
{
    // Lifecycle
    Task UnlockAsync(SecureString masterPassword, CancellationToken ct = default);
    Task LockAsync(CancellationToken ct = default);
    bool IsUnlocked { get; }

    // CRUD
    Task<CredentialEntry> AddAsync(CredentialCreateRequest request, CancellationToken ct = default);
    Task<CredentialEntry> UpdateAsync(Guid id, CredentialUpdateRequest request, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<CredentialEntry>> ListAsync(CancellationToken ct = default);

    // Retrieval (decrypted, short-lived)
    Task<CredentialPayload> GetPayloadAsync(Guid id, CancellationToken ct = default);

    // Maintenance
    Task RotateEncryptionKeyAsync(SecureString newMasterPassword, CancellationToken ct = default);
    Task<byte[]> ExportAsync(CancellationToken ct = default);
    Task ImportAsync(byte[] encryptedBlob, SecureString password, CancellationToken ct = default);
}
```

### 5.2 IRemoteExecutor
```csharp
public interface IRemoteExecutor
{
    Task<RemoteResult> ExecuteAsync(MachineTarget target, RemoteCommand command, CancellationToken ct = default);
    Task TransferFileAsync(MachineTarget target, FileTransferRequest request, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(MachineTarget target, CancellationToken ct = default);
}
```

### 5.3 IPatchService
```csharp
public interface IPatchService
{
    Task<IReadOnlyList<PatchInfo>> DetectAsync(MachineTarget target, CancellationToken ct = default);
    Task<PatchResult> ApplyAsync(MachineTarget target, IReadOnlyList<PatchInfo> patches, PatchOptions options, CancellationToken ct = default);
    Task<PatchResult> VerifyAsync(MachineTarget target, IReadOnlyList<string> patchIds, CancellationToken ct = default);
    Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(Guid machineId, CancellationToken ct = default);
    Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(MachineTarget target, CancellationToken ct = default);
}
```

### 5.4 IServiceController
```csharp
public interface IServiceController
{
    Task<ServiceInfo> GetStatusAsync(MachineTarget target, string serviceName, CancellationToken ct = default);
    Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default);
    Task<ServiceActionResult> ControlAsync(MachineTarget target, string serviceName, ServiceAction action, CancellationToken ct = default);
    Task<IReadOnlyList<ServiceActionResult>> BulkControlAsync(IReadOnlyList<MachineTarget> targets, string serviceName, ServiceAction action, CancellationToken ct = default);
}
```

### 5.5 IInventoryService
```csharp
public interface IInventoryService
{
    Task<Machine> AddAsync(MachineCreateRequest request, CancellationToken ct = default);
    Task<Machine> UpdateAsync(Guid id, MachineUpdateRequest request, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
    Task<Machine?> GetAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default);
    Task<Machine> RefreshMetadataAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Machine>> DiscoverAsync(NetworkRange range, CancellationToken ct = default);
    Task ImportAsync(Stream csvStream, CancellationToken ct = default);
    Task ExportAsync(MachineQuery query, Stream destination, ExportFormat format, CancellationToken ct = default);
}
```

### 5.6 IAuditLogger
```csharp
public interface IAuditLogger
{
    Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default);
    Task<PagedResult<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default);
    Task<long> CountAsync(AuditQuery query, CancellationToken ct = default);
    Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default);
}
```

### 5.7 IJobScheduler
```csharp
public interface IJobScheduler
{
    Task<JobId> SubmitAsync(JobDefinition job, CancellationToken ct = default);
    Task<ScheduleId> ScheduleAsync(JobDefinition job, string cronExpression, CancellationToken ct = default);
    Task CancelAsync(JobId jobId, CancellationToken ct = default);
    Task UnscheduleAsync(ScheduleId scheduleId, CancellationToken ct = default);
    Task<JobStatus> GetStatusAsync(JobId jobId, CancellationToken ct = default);
    Task<PagedResult<JobSummary>> ListJobsAsync(JobQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledJobSummary>> ListSchedulesAsync(CancellationToken ct = default);

    // Real-time progress (for GUI binding)
    IObservable<JobProgressEvent> ProgressStream { get; }
}
```

### 5.8 IAgentGateway
```csharp
public interface IAgentGateway
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    IReadOnlyList<ConnectedAgent> GetConnectedAgents();
    Task<RemoteResult> SendCommandAsync(string agentId, RemoteCommand command, CancellationToken ct = default);
    Task<AgentMetadata> GetMetadataAsync(string agentId, CancellationToken ct = default);
    Task RequestUpdateAsync(string agentId, AgentUpdatePackage package, CancellationToken ct = default);

    IObservable<AgentConnectionEvent> ConnectionEvents { get; }
}
```

### 5.9 ICommandBroker
```csharp
public interface ICommandBroker
{
    /// <summary>
    /// Enqueue a command for async execution. Returns a tracking ID immediately.
    /// The command is processed by a background loop that resolves IRemoteExecutor,
    /// executes the command, persists results to IJobRepository, and emits a
    /// CommandCompletedEvent on CompletedStream.
    /// </summary>
    Task<Guid> SubmitAsync(CommandEnvelope envelope, CancellationToken ct = default);

    /// <summary>
    /// Observable stream of completed command events. GUI ViewModels subscribe
    /// to this for real-time status feedback even after navigating away.
    /// </summary>
    IObservable<CommandCompletedEvent> CompletedStream { get; }
}

public record CommandEnvelope(
    Guid MachineId,
    string MachineName,
    MachineTarget Target,
    RemoteCommand Command,
    Guid? JobId = null,
    string? Description = null);

public record CommandCompletedEvent(
    Guid TrackingId,
    Guid MachineId,
    string MachineName,
    Guid? JobId,
    RemoteResult Result,
    DateTime CompletedUtc);
```

---

## 6. Shared DTOs and Enums

```csharp
// Target resolution — wraps machine + credential for transport layer
public record MachineTarget(
    Guid MachineId,
    string Hostname,
    OsType OsType,
    MachineConnectionMode ConnectionMode,
    TransportProtocol Protocol,         // Ssh, WinRm, PsRemoting, Agent
    int Port,
    Guid CredentialId);

// Common paging
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

// Enumerations
public enum OsType { Windows, Linux }
public enum MachineConnectionMode { Agentless, Agent }
public enum TransportProtocol { Ssh, WinRm, PsRemoting, Agent }
public enum MachineState { Online, Offline, Unreachable, Maintenance }
public enum PatchSeverity { Critical, Important, Moderate, Low, Unclassified }
public enum PatchCategory { Security, BugFix, Feature, Driver, Other }
public enum PatchInstallState { Detected, Staged, Approved, Deferred, Installing, Installed, Failed }
public enum ServiceState { Running, Stopped, Starting, Stopping, Paused, Unknown }
public enum ServiceStartupType { Automatic, Manual, Disabled }
public enum ServiceAction { Start, Stop, Restart, Enable, Disable }
public enum AuditAction
{
    MachineAdded, MachineRemoved, MachineMetadataRefreshed,
    PatchScanStarted, PatchScanCompleted,
    PatchApproved, PatchDeferred, PatchInstallStarted, PatchInstallCompleted, PatchInstallFailed,
    ServiceStarted, ServiceStopped, ServiceRestarted,
    CredentialCreated, CredentialUpdated, CredentialDeleted, CredentialAccessed,
    VaultUnlocked, VaultLocked,
    JobSubmitted, JobCompleted, JobFailed, JobCancelled,
    AgentConnected, AgentDisconnected, AgentUpdated,
    UserLogin, UserLogout, SettingsChanged
}
public enum AuditOutcome { Success, Failure, PartialSuccess }
public enum ExportFormat { Csv, Json }
```
