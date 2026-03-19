# Module Descriptions and Responsibilities

## Module 1: Credential Vault (`HomeManagement.Vault`)

### Purpose
Securely store, retrieve, and manage credentials used to authenticate against remote machines. Credentials never leave the vault in plaintext except when actively used for a connection, and are zeroed from memory immediately after.

### Responsibilities
- Encrypt/decrypt credentials at rest using AES-256-GCM
- Integrate with OS-native secret stores (DPAPI on Windows, libsecret/kwallet on Linux)
- Support credential types: username/password, SSH private key, SSH key + passphrase, Kerberos keytab
- Associate credentials with machines or machine groups
- Provide a master-password unlock flow at application startup
- Rotate vault encryption key on demand
- Export/import vault (encrypted blob) for backup

### Key Types
```
CredentialEntry {
    Id: Guid
    Label: string
    Type: enum { Password, SshKey, SshKeyWithPassphrase, Kerberos }
    Username: string
    EncryptedPayload: byte[]       // AES-256-GCM encrypted
    AssociatedMachineIds: Guid[]
    CreatedUtc: DateTime
    LastUsedUtc: DateTime?
    LastRotatedUtc: DateTime?
}
```

### Threat Considerations
- Vault file encrypted with a key derived from master password via Argon2id (memory-hard KDF)
- Key material pinned in memory ("secure string" pattern), cleared after use
- Vault lock after configurable idle timeout
- No credential echoed in logs — ever

---

## Module 2: Remote Execution Layer (`HomeManagement.Transport`)

### Purpose
Abstract all remote command execution behind a unified interface, regardless of transport protocol (SSH, WinRM, PowerShell Remoting, or agent).

### Responsibilities
- Establish and manage connections to remote machines
- Execute commands and return structured results (exit code, stdout, stderr)
- Transfer files to/from remote machines
- Maintain a connection pool with configurable limits and timeouts
- Support interactive and non-interactive execution modes
- Route execution through the Agent Gateway when a machine uses agent mode

### Transport Providers

| Provider | Target OS | Protocol | Port |
|---|---|---|---|
| `SshTransport` | Linux / macOS | SSH v2 | 22 |
| `WinRmTransport` | Windows | WS-Management over HTTP(S) | 5985/5986 |
| `PsRemotingTransport` | Windows | PowerShell Remoting (WSMAN) | 5985/5986 |
| `AgentTransport` | Any | gRPC over mTLS | 9444 |

### Key Interface
```csharp
public interface IRemoteExecutor
{
    Task<RemoteResult> ExecuteAsync(
        MachineTarget target,
        RemoteCommand command,
        CancellationToken ct = default);

    Task TransferFileAsync(
        MachineTarget target,
        FileTransferRequest request,
        CancellationToken ct = default);

    Task<bool> TestConnectionAsync(
        MachineTarget target,
        CancellationToken ct = default);
}

public record RemoteResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut);

public record RemoteCommand(
    string CommandText,
    TimeSpan Timeout,
    bool ElevatedExecution = false,
    IDictionary<string, string>? EnvironmentVariables = null);
```

### Connection Lifecycle
```
  TestConnection  →  Acquire from Pool / Open New  →  Execute  →  Return to Pool
       │                                                              │
       └── Fail ──→ Retry (exponential backoff) ──→ Circuit Break ───┘
```

---

## Module 3: Patch Detection & Application (`HomeManagement.Patching`)

### Purpose
Detect available patches on remote machines and apply them in a controlled, auditable manner.

### Responsibilities
- **Detection**: Query each machine for available updates using OS-native tooling
- **Classification**: Categorize patches (security, critical, optional, driver)
- **Approval Workflow**: Stage patches for review before application
- **Application**: Install approved patches with reboot handling
- **Verification**: Confirm patches were applied successfully post-install
- **History**: Record every patch event per machine

### OS-Specific Strategies

| OS | Detection Tool | Install Tool | Reboot Method |
|---|---|---|---|
| Windows | `PSWindowsUpdate` / Windows Update COM API | `Install-WindowsUpdate` | `Restart-Computer` |
| Ubuntu/Debian | `apt list --upgradable` | `apt-get upgrade -y` | `shutdown -r` |
| RHEL/CentOS/Fedora | `dnf check-update` / `yum check-update` | `dnf upgrade -y` | `shutdown -r` |
| SUSE | `zypper list-updates` | `zypper update -y` | `shutdown -r` |

### Patch Lifecycle State Machine
```
  ┌──────────┐     ┌──────────┐     ┌──────────┐     ┌──────────┐     ┌──────────┐
  │ Detected │────→│ Staged   │────→│ Approved │────→│ Installing│───→│ Installed│
  └──────────┘     └────┬─────┘     └──────────┘     └─────┬─────┘    └──────────┘
                        │                                   │
                        ▼                                   ▼
                   ┌──────────┐                       ┌──────────┐
                   │ Deferred │                       │  Failed  │
                   └──────────┘                       └──────────┘
```

### Key Interface
```csharp
public interface IPatchService
{
    Task<IReadOnlyList<PatchInfo>> DetectAsync(
        MachineTarget target, CancellationToken ct = default);

    Task<PatchResult> ApplyAsync(
        MachineTarget target,
        IReadOnlyList<PatchInfo> patches,
        PatchOptions options,
        CancellationToken ct = default);

    Task<IReadOnlyList<PatchHistoryEntry>> GetHistoryAsync(
        Guid machineId, CancellationToken ct = default);
}

public record PatchInfo(
    string PatchId,
    string Title,
    PatchSeverity Severity,
    PatchCategory Category,
    string Description,
    long SizeBytes,
    bool RequiresReboot,
    DateTime PublishedUtc);

public record PatchOptions(
    bool AllowReboot = false,
    TimeSpan RebootDelay = default,
    bool DryRun = false,
    int MaxConcurrentMachines = 5);
```

---

## Module 4: Service Controller (`HomeManagement.Services`)

### Purpose
Manage system services (daemons) on remote machines — start, stop, restart, query status, and configure startup type.

### Responsibilities
- Query service status on remote machines
- Start / Stop / Restart services
- Change service startup type (automatic, manual, disabled)
- Monitor service health over time
- Bulk operations across machine groups
- Service dependency awareness

### OS-Specific Strategies

| OS | Service Manager | Status Command | Control Commands |
|---|---|---|---|
| Windows | `sc.exe` / `Get-Service` | `Get-Service -Name X` | `Start-Service`, `Stop-Service`, `Restart-Service` |
| Linux (systemd) | `systemctl` | `systemctl status X` | `systemctl start/stop/restart X` |
| Linux (SysVinit) | `service` | `service X status` | `service X start/stop/restart` |

### Key Interface
```csharp
public interface IServiceController
{
    Task<ServiceInfo> GetStatusAsync(
        MachineTarget target, string serviceName, CancellationToken ct = default);

    Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(
        MachineTarget target, ServiceFilter? filter = null, CancellationToken ct = default);

    Task<ServiceActionResult> ControlAsync(
        MachineTarget target,
        string serviceName,
        ServiceAction action,           // Start, Stop, Restart, Enable, Disable
        CancellationToken ct = default);

    Task<IReadOnlyList<ServiceActionResult>> BulkControlAsync(
        IReadOnlyList<MachineTarget> targets,
        string serviceName,
        ServiceAction action,
        CancellationToken ct = default);
}

public record ServiceInfo(
    string Name,
    string DisplayName,
    ServiceState State,                 // Running, Stopped, Starting, Stopping, Unknown
    ServiceStartupType StartupType,     // Auto, Manual, Disabled
    int? ProcessId,
    TimeSpan? Uptime,
    string[] Dependencies);
```

---

## Module 5: Machine Inventory & Metadata (`HomeManagement.Inventory`)

### Purpose
Maintain a registry of all managed machines with their metadata, group membership, and current state.

### Responsibilities
- CRUD operations on machine records
- Store and refresh machine metadata (OS version, CPU, RAM, disk, IP addresses)
- Group machines by tags, roles, locations, or OS type
- Track machine reachability and last-contact timestamps
- Import machines from CSV / network scan / Active Directory
- Export inventory reports

### Key Types
```csharp
public record Machine(
    Guid Id,
    string Hostname,
    string? Fqdn,
    IPAddress[] IpAddresses,
    OsType OsType,                      // Windows, Linux
    string OsVersion,
    MachineConnectionMode ConnectionMode, // Agentless, Agent
    Guid CredentialId,
    MachineState State,                 // Online, Offline, Unreachable, Maintenance
    Dictionary<string, string> Tags,
    HardwareInfo? Hardware,
    DateTime AddedUtc,
    DateTime LastContactUtc);

public record HardwareInfo(
    int CpuCores,
    long RamBytes,
    DiskInfo[] Disks,
    string Architecture);
```

### Key Interface
```csharp
public interface IInventoryService
{
    Task<Machine> AddAsync(MachineCreateRequest request, CancellationToken ct = default);
    Task<Machine> UpdateAsync(Guid id, MachineUpdateRequest request, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
    Task<Machine?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Machine>> QueryAsync(MachineQuery query, CancellationToken ct = default);
    Task<Machine> RefreshMetadataAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Machine>> DiscoverAsync(NetworkRange range, CancellationToken ct = default);
}
```

---

## Module 6: Logging & Audit Trail (`HomeManagement.Auditing`)

### Purpose
Provide structured, tamper-evident logging of every action taken through the system for security auditing and operational troubleshooting.

### Responsibilities
- Record every user action, remote command, and system event
- Structured JSON logging with correlation IDs
- Separate audit log (immutable events) from operational log (debug/info)
- Log rotation and retention policies
- Queryable audit history via the GUI
- Export audit logs to external systems (syslog, file, future SIEM)

### Audit Event Schema
```csharp
public record AuditEvent(
    Guid EventId,
    DateTime TimestampUtc,
    string CorrelationId,
    AuditAction Action,                 // e.g., PatchApplied, ServiceRestarted, CredentialAccessed
    string ActorIdentity,               // local user who triggered the action
    Guid? TargetMachineId,
    string? TargetMachineName,
    string? Detail,                     // human-readable description
    Dictionary<string, string>? Properties,
    AuditOutcome Outcome,               // Success, Failure, PartialSuccess
    string? ErrorMessage);
```

### Key Interface
```csharp
public interface IAuditLogger
{
    Task RecordAsync(AuditEvent auditEvent, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditQuery query, CancellationToken ct = default);
    Task<long> CountAsync(AuditQuery query, CancellationToken ct = default);
    Task ExportAsync(AuditQuery query, Stream destination, ExportFormat format, CancellationToken ct = default);
}
```

---

## Module 7: Job Orchestrator & Scheduler (`HomeManagement.Orchestration`)

### Purpose
Coordinate multi-step operations across multiple machines, manage execution order, parallelism, and scheduling.

### Responsibilities
- Execute jobs (patch runs, service operations) across machine groups
- Enforce concurrency limits and rolling-update patterns
- Schedule recurring jobs (e.g., "patch scan every Sunday 2 AM")
- Maintain job history with per-machine results
- Support cancellation of in-flight jobs
- Pre/post execution hooks (e.g., snapshot before patching)

### Key Interface
```csharp
public interface IJobScheduler
{
    Task<JobId> SubmitAsync(JobDefinition job, CancellationToken ct = default);
    Task<ScheduleId> ScheduleAsync(JobDefinition job, CronExpression cron, CancellationToken ct = default);
    Task CancelAsync(JobId jobId, CancellationToken ct = default);
    Task<JobStatus> GetStatusAsync(JobId jobId, CancellationToken ct = default);
    Task<IReadOnlyList<JobSummary>> ListJobsAsync(JobQuery query, CancellationToken ct = default);
}

public record JobDefinition(
    string Name,
    JobType Type,                       // PatchScan, PatchApply, ServiceControl, Custom
    IReadOnlyList<Guid> TargetMachineIds,
    Dictionary<string, object> Parameters,
    int MaxParallelism = 5,
    RetryPolicy RetryPolicy = default);
```

### Async Command Pipeline

When `JobExecutionQuartzJob` fires, it deserializes the job's `DefinitionJson` to resolve target machines and build OS-appropriate commands. Rather than executing commands synchronously, it submits each command through the **Command Broker** (`ICommandBroker`) for asynchronous, queued dispatch:

```csharp
public interface ICommandBroker
{
    Task<Guid> SubmitAsync(CommandEnvelope envelope, CancellationToken ct = default);
    IObservable<CommandCompletedEvent> CompletedStream { get; }
}
```

- `SubmitAsync` enqueues a `CommandEnvelope` into a bounded `Channel<T>` (capacity 256) and returns a tracking ID immediately.
- A background processing loop drains the channel, resolves `IRemoteExecutor` from DI scope, executes the command, persists results to `IJobRepository`, and emits a `CommandCompletedEvent` via Rx `Subject<T>`.
- The GUI subscribes to `CompletedStream` for real-time status updates, allowing the user to navigate away from the originating page without losing results.

This fire-and-forget pattern ensures that long-running multi-machine operations complete reliably even if the user moves on.

---

## Module 8: GUI Application (`HomeManagement.Gui`)

### Purpose
Provide a rich desktop GUI for operators to manage machines, view status, trigger operations, and review audit logs.

### Responsibilities
- Dashboard with fleet-wide health summary
- Machine inventory browser with search and filtering
- Patch management view (detect → review → approve → apply)
- Service controller panel with bulk operations
- Live job progress tracking
- Credential vault management UI
- Audit log viewer with filtering and export
- Settings and configuration

### Technology
- **Avalonia UI** with **ReactiveUI** (MVVM)
- Runs on Windows, Linux, and macOS
- All business logic invoked through the Core API interfaces — the GUI never directly touches transport or data layers

### View Structure
```
MainWindow
├── NavigationPanel (sidebar)
│   ├── Dashboard
│   ├── Machines
│   ├── Patching
│   ├── Services
│   ├── Jobs
│   ├── Audit Log
│   └── Settings
├── ContentArea (dynamic, view-per-section)
└── StatusBar (connection count, active jobs, notifications)
```

---

## Module 9: Optional Lightweight Agent (`HomeManagement.Agent`)

### Purpose
Run on remote machines that cannot be reached via direct SSH/WinRM (e.g., behind NAT, firewall restrictions). The agent initiates an outbound connection to the control machine.

### Responsibilities
- Establish persistent gRPC channel to the control machine (outbound connection)
- Execute commands received from the control machine
- Report system metadata and health
- Self-update on command
- Minimal footprint (~10 MB, single binary)
- Run as a system service (Windows Service / systemd unit)

### Communication Model
```
Agent (remote machine)  ──── gRPC bidirectional stream ────→  Control Machine
                              (mTLS, agent cert + CA cert)

Agent opens the connection.
Control machine pushes commands over the stream.
Agent returns results over the stream.
```

### Inbound Command Queue

The agent's `AgentHostService` decouples message reception from command execution using an inbound `Channel<CommandRequest>` (bounded capacity 128). The gRPC receive loop writes incoming commands into the channel without blocking, while a separate `CommandProcessingLoopAsync` drains the channel through the existing `SemaphoreSlim`-bounded `CommandDispatcher`. This prevents slow commands from blocking update directives, shutdown signals, and heartbeat acknowledgments.

### Self-Contained Deployment
```
hm-agent                    (single executable)
hm-agent.json               (configuration: control machine address, cert paths)
certs/
  agent.pfx                 (agent identity certificate)
  ca.crt                    (control machine CA certificate)
```
