# 08 — Subsystem Internal Architectures

> **Version:** 1.0  
> **Date:** 2026-03-14  
> **Status:** Approved  
> **Audience:** Developers implementing module internals

This document specifies the internal architecture of every subsystem in the HomeManagement platform. Each section is self-contained and covers: purpose, internal modules/classes, data structures, API surface, error handling, security, logging, and integration points.

---

## Table of Contents

1. [Credential Vault](#1-credential-vault)
2. [Remote Execution Engine](#2-remote-execution-engine)
3. [Patch Manager](#3-patch-manager)
4. [Service Controller](#4-service-controller)
5. [Machine Inventory](#5-machine-inventory)
6. [Logging / Audit System](#6-logging--audit-system)
7. [GUI Backend](#7-gui-backend)

---

## 1. Credential Vault

**Project:** `HomeManagement.Vault`  
**Namespace:** `HomeManagement.Vault`  
**Implements:** `ICredentialVault`

### 1.1 Purpose & Responsibilities

The Credential Vault is the **single trusted custodian** of all authentication material used to connect to managed machines. It provides:

- **Encryption at rest** — all credentials encrypted with AES-256-GCM, keyed by Argon2id-derived master key
- **Memory safety** — decrypted material pinned with `GCHandle`, zeroed on dispose
- **Unlock/lock lifecycle** — vault is sealed by default; master password unlocks the derived key into protected memory
- **Key rotation** — re-encrypt all entries under a new master password without data loss
- **Import/export** — portable encrypted blobs for backup and migration

### 1.2 Internal Modules & Classes

```
HomeManagement.Vault/
├── CredentialVaultService.cs        # ICredentialVault implementation — orchestrates all operations
├── VaultModuleRegistration.cs       # IModuleRegistration — self-registers into DI
├── Crypto/
│   ├── MasterKeyDerivation.cs       # Argon2id KDF wrapper (salt generation, parameter tuning)
│   ├── AesGcmEncryptor.cs           # AES-256-GCM encrypt/decrypt with nonce management
│   └── KeyProtector.cs              # Holds derived key in pinned memory, zeroes on lock
├── Storage/
│   ├── VaultFileStore.cs            # Reads/writes the encrypted vault file (JSON envelope)
│   └── VaultEnvelope.cs             # Data structure for the on-disk format
└── Mappers/
    └── CredentialMapper.cs          # Maps between VaultEntry ↔ CredentialEntry/CredentialPayload
```

### 1.3 Data Structures

#### On-Disk Vault Envelope (JSON, encrypted payload)

```
┌─────────────────────────────────────────────────┐
│ VaultEnvelope                                   │
│  ├─ FormatVersion: int (1)                      │
│  ├─ Kdf: "argon2id"                             │
│  ├─ KdfParams: { memory, iterations, parallel } │
│  ├─ Salt: byte[16]                              │
│  ├─ Nonce: byte[12]   (AES-GCM)                │
│  ├─ Tag: byte[16]     (AES-GCM auth tag)        │
│  └─ CiphertextBase64: string                    │
│       └─ Decrypts to: VaultEntry[]              │
└─────────────────────────────────────────────────┘
```

#### Internal Types

```csharp
// Plaintext representation held only in memory while vault is unlocked
internal record VaultEntry(
    Guid Id,
    string Label,
    CredentialType Type,
    string Username,
    byte[] EncryptedPayload,    // Per-entry encryption (inner layer)
    byte[] EntryNonce,
    byte[] EntryTag,
    Guid[] AssociatedMachineIds,
    DateTime CreatedUtc,
    DateTime? LastUsedUtc,
    DateTime? LastRotatedUtc);

// Holds the master-derived key in pinned memory
internal sealed class KeyProtector : IDisposable
{
    private GCHandle _handle;
    private byte[] _key;       // 32 bytes, AES-256
    public ReadOnlySpan<byte> Key { get; }
    public void Dispose();      // ZeroMemory + Free handle
}

// On-disk format
internal record VaultEnvelope(
    int FormatVersion,
    string Kdf,
    KdfParams KdfParams,
    byte[] Salt,
    byte[] Nonce,
    byte[] Tag,
    string CiphertextBase64);

internal record KdfParams(int MemoryKiB, int Iterations, int Parallelism);
```

### 1.4 API Surface

Implements `ICredentialVault` (see [ICredentialVault.cs](../../src/HomeManagement.Abstractions/Interfaces/ICredentialVault.cs)):

| Method | Description |
|---|---|
| `UnlockAsync(SecureString, ct)` | Derive key via Argon2id, decrypt vault, hold entries in memory |
| `LockAsync(ct)` | Zero the `KeyProtector`, clear entry cache, flush to disk |
| `IsUnlocked` | Returns whether the derived key is currently held |
| `AddAsync(request, ct)` | Encrypt payload, add `VaultEntry`, persist |
| `UpdateAsync(id, request, ct)` | Re-encrypt if payload changed, update fields, persist |
| `RemoveAsync(id, ct)` | Remove entry, persist |
| `ListAsync(ct)` | Return `CredentialEntry` metadata (no decrypted payloads) |
| `GetPayloadAsync(id, ct)` | Decrypt per-entry payload into `CredentialPayload` (caller must dispose) |
| `RotateEncryptionKeyAsync(newPassword, ct)` | Re-derive key, re-encrypt all entries, persist |
| `ExportAsync(ct)` | Return the raw encrypted vault file bytes |
| `ImportAsync(blob, password, ct)` | Validate, decrypt with provided password, merge or replace |

### 1.5 Operational Flow — Unlock → Use → Lock

```
┌─────────┐    UnlockAsync(pw)    ┌──────────────────┐
│  SEALED  │ ──────────────────►  │    UNLOCKED       │
│          │                      │ KeyProtector alive │
│ No key   │  ◄──────────────────  │ Entries in memory │
│ in memory│    LockAsync()       │                    │
└─────────┘                      └──────────────────┘
                                       │
                                  GetPayloadAsync(id)
                                       │
                                       ▼
                                 CredentialPayload
                                 (IDisposable — caller
                                  zeroes on dispose)
```

### 1.6 Error Handling

| Condition | Exception | Behavior |
|---|---|---|
| Vault locked when credential requested | `InvalidOperationException` | Prompt user to unlock |
| Wrong master password | `CryptographicException` | AES-GCM tag mismatch; log attempt, increment fail counter |
| Corrupt vault file | `VaultCorruptException` (custom) | Log critical, suggest restore from export |
| Entry not found | `KeyNotFoundException` | 404 equivalent in API layer |
| Concurrent unlock attempts | `SemaphoreSlim` guard | Second caller waits, no double-derive |

### 1.7 Security Considerations

1. **Argon2id parameters:** 64 MiB memory, 3 iterations, 4 parallelism (tuned for desktop; configurable)
2. **Two-layer encryption:** Outer layer encrypts the entire vault; inner layer encrypts each entry payload individually so bulk decryption of all payloads is never needed
3. **Nonce management:** Each encrypt operation generates a fresh 12-byte `RandomNumberGenerator` nonce — never reuse
4. **Memory pinning:** `GCHandle.Alloc(Pinned)` prevents GC from copying keys; `CryptographicOperations.ZeroMemory()` on dispose
5. **No plaintext to disk:** Vault file is always the encrypted envelope; temp files use `FileOptions.DeleteOnClose`
6. **SecureString for master password:** Consumed once during derivation, never stored
7. **Failed-attempt throttling:** Exponential backoff after 3 failed unlocks (1s, 2s, 4s, 8s…)
8. **Audit integration:** Every `GetPayloadAsync`, `UnlockAsync`, `RotateEncryptionKeyAsync` emits an audit event

### 1.8 Logging Strategy

| Level | Events |
|---|---|
| `Information` | Vault unlocked, locked, credential added/updated/removed, key rotated |
| `Warning` | Failed unlock attempt (wrong password), missing entry |
| `Error` | Vault corruption detected, I/O failure |
| `Debug` | KDF timing (e.g., "Argon2id derivation completed in 420ms"), entry count after load |

**Sensitive data rules:** Never log credential payloads, master password, or derived keys. Use `ISensitiveDataFilter.Redact()` before writing any detail string containing user input.

### 1.9 Integration Points

| Subsystem | Direction | Mechanism |
|---|---|---|
| **Remote Execution Engine** | ← consumes | Calls `GetPayloadAsync(credentialId)` to authenticate SSH/WinRM sessions |
| **Machine Inventory** | ← consumes | References `CredentialId` on each `Machine`; vault validates existence on machine add |
| **Audit System** | → produces | Emits `CredentialCreated`, `CredentialAccessed`, `VaultUnlocked`, `VaultLocked` audit events |
| **GUI Backend** | ← consumes | Vault management UI calls `ListAsync`, `AddAsync`, `UnlockAsync`, etc. |
| **ICorrelationContext** | ← consumes | Stamps correlation ID on all vault operations for traceability |

---

## 2. Remote Execution Engine

**Project:** `HomeManagement.Transport`  
**Namespace:** `HomeManagement.Transport`  
**Implements:** `IRemoteExecutor`, `IRemotePathResolver`

### 2.1 Purpose & Responsibilities

The Remote Execution Engine is the **transport abstraction layer** that enables every other subsystem to execute commands, transfer files, and test connectivity on remote machines without knowledge of the underlying protocol. It:

- Routes commands to the correct transport provider based on `TransportProtocol`
- Manages connection pooling and lifecycle
- Applies resilience (retry, circuit-breaker, timeout) via `IResiliencePipeline`
- Resolves OS-specific path conventions
- Reports structured connection-test results
- Provides fire-and-forget async command queuing via `ICommandBroker` / `CommandBrokerService` — GUI dispatches commands into a bounded `Channel<T>` (capacity 256); a background loop drains the queue, executes via scoped `IRemoteExecutor`, persists results to `IJobRepository`, and emits `CommandCompletedEvent` on a reactive `CompletedStream` so the UI auto-refreshes even if the user navigated away

### 2.2 Internal Modules & Classes

```
HomeManagement.Transport/
├── TransportModuleRegistration.cs         # IModuleRegistration — registers all transport types
├── RemoteExecutorRouter.cs                # IRemoteExecutor — routes to correct provider by protocol
├── RemotePathResolverImpl.cs              # IRemotePathResolver — OS-aware path normalization
├── CommandBrokerService.cs                # ICommandBroker — fire-and-forget async command queue:
│                                          #   bounded Channel<QueuedCommand>(256), background ProcessLoopAsync,
│                                          #   scoped IRemoteExecutor per command, IJobRepository for persistence,
│                                          #   Subject<CommandCompletedEvent> exposed as CompletedStream (IObservable)
├── Providers/
│   ├── ITransportProvider.cs              # Internal interface — each protocol implements this
│   ├── SshTransportProvider.cs            # SSH.NET-based provider (Linux primary)
│   ├── WinRmTransportProvider.cs          # WinRM/WS-Man provider (Windows agentless)
│   ├── PSRemotingTransportProvider.cs     # PowerShell Remoting via System.Management.Automation
│   └── AgentTransportProvider.cs          # Delegates to IAgentGateway for agent-mode machines
├── ConnectionPool/
│   ├── ConnectionPoolManager.cs           # Per-host connection pool with idle timeout
│   ├── PooledConnection.cs                # Wrapper around a live transport session
│   └── ConnectionPoolOptions.cs           # MaxPerHost, IdleTimeout, MaxLifetime
├── Elevation/
│   └── ElevationHandler.cs               # Wraps commands with sudo/RunAs based on ElevationMode
└── Mappers/
    └── ResultMapper.cs                    # Maps provider-specific results → RemoteResult
```

### 2.3 Data Structures

```csharp
// Internal transport provider interface (not in Abstractions — transport-local)
internal interface ITransportProvider
{
    TransportProtocol Protocol { get; }
    Task<RemoteResult> ExecuteAsync(TransportSession session, RemoteCommand command, CancellationToken ct);
    Task TransferFileAsync(TransportSession session, FileTransferRequest request, IProgress<TransferProgress>? progress, CancellationToken ct);
    Task<ConnectionTestResult> TestConnectionAsync(TransportSession session, CancellationToken ct);
}

// Represents an authenticated session to a remote host
internal sealed class TransportSession : IAsyncDisposable
{
    public Guid MachineId { get; }
    public Hostname Hostname { get; }
    public TransportProtocol Protocol { get; }
    public int Port { get; }
    public DateTime CreatedUtc { get; }
    public DateTime LastUsedUtc { get; set; }
    public object NativeClient { get; }  // SshClient, WSManClient, PowerShell runspace, etc.
    public ValueTask DisposeAsync();
}

// Connection pool configuration
internal record ConnectionPoolOptions(
    int MaxConnectionsPerHost = 3,
    TimeSpan IdleTimeout = default,       // default: 5 min
    TimeSpan MaxLifetime = default);      // default: 30 min

// Elevation context produced by ElevationHandler
internal record ElevatedCommand(
    string WrappedCommandText,
    bool RequiresStdinPassword);

// CommandBrokerService types (fire-and-forget command pipeline)
public record CommandEnvelope(
    MachineTarget Target,
    RemoteCommand Command,
    Guid? JobId = null);

public record CommandCompletedEvent(
    Guid TrackingId,
    Guid MachineId,
    RemoteResult Result,
    Guid? JobId);

internal record QueuedCommand(
    Guid TrackingId,
    CommandEnvelope Envelope);
```

### 2.4 API Surface

Implements `IRemoteExecutor` and `IRemotePathResolver`:

| Method | Description |
|---|---|
| `ExecuteAsync(target, command, ct)` | Resolve credential → acquire connection → apply elevation → execute → return `RemoteResult` |
| `TransferFileAsync(target, request, progress, ct)` | Resolve credential → acquire connection → SFTP/WinRM transfer with progress reporting |
| `TestConnectionAsync(target, ct)` | Connect, detect OS, measure latency, return `ConnectionTestResult` |
| `NormalizePath(path, targetOs)` | Convert to OS-native path separators |
| `CombinePath(targetOs, segments)` | Join segments with OS-appropriate separator |
| `GetSeparator(targetOs)` | Return `'/'` or `'\\'` |

### 2.5 Routing & Connection Flow

```
            ExecuteAsync(target, command)
                       │
                       ▼
              ┌─────────────────┐
              │ RemoteExecutor   │
              │   Router         │
              └────────┬────────┘
                       │ target.Protocol
          ┌────────────┼────────────┬──────────────┐
          ▼            ▼            ▼              ▼
     ┌─────────┐ ┌─────────┐ ┌─────────┐   ┌──────────┐
     │  SSH    │ │  WinRM  │ │PSRemoting│   │  Agent   │
     │Provider │ │Provider │ │Provider  │   │Provider  │
     └────┬────┘ └────┬────┘ └────┬────┘   └────┬─────┘
          │            │           │              │
          ▼            ▼           ▼              ▼
    ┌──────────────────────────────────────────────────┐
    │            ConnectionPoolManager                  │
    │   ┌───────────────────────────────────────────┐  │
    │   │ Pool[host:port] → PooledConnection[]       │  │
    │   └───────────────────────────────────────────┘  │
    └──────────────────────────────────────────────────┘
          │                                    │
          ▼                                    ▼
   ICredentialVault                    IResiliencePipeline
   .GetPayloadAsync()                  .ExecuteAsync(targetKey, ...)
```

### 2.6 Error Handling

| Condition | Exception/Result | Behavior |
|---|---|---|
| Unknown protocol | `NotSupportedException` | Fail fast — no retry |
| Authentication failure | `RemoteResult(ExitCode: -1, Stderr: ...)` | Mark `ErrorCategory.Authentication`, break circuit after 3 failures |
| Command timeout | `RemoteResult(TimedOut: true)` | Respect `RemoteCommand.Timeout`; do not retry timed-out commands |
| Connection refused | Wrapped in resilience pipeline | Retry with backoff; open circuit after threshold |
| SSH host key changed | `SecurityException` | Fail immediately — potential MITM; log `Error` |
| File transfer SHA mismatch | `InvalidDataException` | Retry once, then fail with integrity error |
| Vault locked | `InvalidOperationException` from vault | Surface to caller — cannot authenticate |

### 2.7 Security Considerations

1. **No credential caching in transport layer** — always fetch from `ICredentialVault.GetPayloadAsync()` per connection; credential is `IDisposable` and zeroed after session is established
2. **SSH host key verification** — maintain a known-hosts store; reject changed keys (no auto-accept)
3. **Connection pool isolation** — pooled connections are per `(host, port, credentialId)` tuple; never share sessions across credentials
4. **Command injection prevention** — `RemoteCommand.CommandText` is sent as-is to the remote shell; the validated `Hostname` and `ServiceName` types prevent injection through parameters
5. **Elevation safety** — `ElevationHandler` constructs `sudo -S -u <user>` or PowerShell `Start-Process -Verb RunAs` patterns; user input is never interpolated into the elevation wrapper
6. **TLS for WinRM** — enforce HTTPS for WS-Man connections; reject plaintext HTTP unless explicitly configured
7. **No secrets in logs** — connection strings, passwords, and key material are never logged; `ISensitiveDataFilter` applied to all diagnostic output

### 2.8 Logging Strategy

| Level | Events |
|---|---|
| `Information` | Connection established/closed, command dispatched (command text redacted if contains secrets), file transfer start/complete |
| `Warning` | Connection timeout, retry triggered, pool exhaustion |
| `Error` | Authentication failure, host key mismatch, unexpected disconnection |
| `Debug` | Pool stats (active/idle counts), latency measurements, command exit codes, bytes transferred |

All log entries include: `CorrelationId`, `MachineId`, `Hostname`, `Protocol`.

### 2.9 Integration Points

| Subsystem | Direction | Mechanism |
|---|---|---|
| **Credential Vault** | → consumes | `GetPayloadAsync(credentialId)` for SSH keys / passwords |
| **Patch Manager** | ← consumed by | Executes OS-specific patch detection/install commands |
| **Service Controller** | ← consumed by | Executes `systemctl` / `sc.exe` commands |
| **Machine Inventory** | ← consumed by | Executes metadata-gathering commands, network discovery probes |
| **Agent Gateway** | → consumes | `AgentTransportProvider` delegates to `IAgentGateway.SendCommandAsync` |
| **Resilience Pipeline** | → consumes | Wraps every transport operation for retry/circuit-breaker |
| **Audit System** | → produces | Connection events logged as audit events on failure |
| **ICorrelationContext** | → consumes | Propagates correlation ID through the full execution chain |
| **GUI ViewModels** | ← consumed by | `ServicesViewModel`, `PatchingViewModel` dispatch commands via `ICommandBroker.SubmitAsync()` and subscribe to `CompletedStream` for auto-refresh |
| **Job Orchestrator** | ← consumed by | `JobExecutionQuartzJob` dispatches per-machine commands through `ICommandBroker` |

---

## 3. Patch Manager

**Project:** `HomeManagement.Patching`  
**Namespace:** `HomeManagement.Patching`  
**Implements:** `IPatchService`

### 3.1 Purpose & Responsibilities

The Patch Manager is the **cross-platform patch lifecycle engine**. It:

- Detects available patches on remote machines (Windows Update / `apt` / `yum` / `dnf`)
- Applies patches in a controlled, auditable manner with rollback awareness
- Tracks patch history persistently
- Supports streaming for large patch sets
- Provides dry-run mode for pre-flight validation

### 3.2 Internal Modules & Classes

```
HomeManagement.Patching/
├── PatchModuleRegistration.cs             # IModuleRegistration — self-registers into DI
├── PatchServiceImpl.cs                    # IPatchService implementation — orchestrates detect/apply/verify
├── Strategies/
│   ├── IPatchStrategy.cs                  # Internal strategy interface per OS
│   ├── WindowsPatchStrategy.cs            # Windows Update via PowerShell (PSWindowsUpdate / WUA COM)
│   ├── AptPatchStrategy.cs               # Debian/Ubuntu: apt list --upgradable, apt-get upgrade
│   ├── YumDnfPatchStrategy.cs            # RHEL/CentOS/Fedora: yum/dnf check-update, yum/dnf update
│   └── PatchStrategyFactory.cs           # Selects strategy by OsType + OsVersion
├── Parsers/
│   ├── IPatchOutputParser.cs             # Parses raw command output into PatchInfo/PatchResult
│   ├── WindowsUpdateParser.cs            # Parses PSWindowsUpdate JSON output
│   ├── AptOutputParser.cs               # Parses apt output format
│   └── YumDnfOutputParser.cs            # Parses yum/dnf output format
├── Scripts/
│   └── EmbeddedScripts.cs               # Embedded resource loader for PowerShell/bash scripts
└── Mappers/
    └── PatchHistoryMapper.cs            # Maps PatchResult outcomes → PatchHistoryEntry for persistence
```

### 3.3 Data Structures

```csharp
// Internal strategy interface
internal interface IPatchStrategy
{
    OsType TargetOs { get; }
    Task<IReadOnlyList<PatchInfo>> DetectAsync(IRemoteExecutor executor, MachineTarget target, CancellationToken ct);
    IAsyncEnumerable<PatchInfo> DetectStreamAsync(IRemoteExecutor executor, MachineTarget target, CancellationToken ct);
    Task<PatchResult> ApplyAsync(IRemoteExecutor executor, MachineTarget target, IReadOnlyList<PatchInfo> patches, PatchOptions options, CancellationToken ct);
    Task<PatchResult> VerifyAsync(IRemoteExecutor executor, MachineTarget target, IReadOnlyList<string> patchIds, CancellationToken ct);
    Task<IReadOnlyList<InstalledPatch>> GetInstalledAsync(IRemoteExecutor executor, MachineTarget target, CancellationToken ct);
}

// Parser output — intermediate representation before mapping to PatchInfo
internal record RawPatchEntry(
    string Id,
    string Title,
    string SeverityRaw,
    string CategoryRaw,
    string Description,
    long SizeBytes,
    bool RequiresReboot,
    DateTime? PublishedUtc);

// Embedded script descriptor
internal record EmbeddedScript(string ResourceName, string ScriptContent, OsType TargetOs);
```

### 3.4 API Surface

Implements `IPatchService` (see [IPatchService.cs](../../src/HomeManagement.Abstractions/Interfaces/IPatchService.cs)):

| Method | Description |
|---|---|
| `DetectAsync(target, ct)` | Select strategy → execute remote detect command → parse → return `PatchInfo[]` |
| `DetectStreamAsync(target, ct)` | Same but yields `IAsyncEnumerable<PatchInfo>` as output is parsed line-by-line |
| `ApplyAsync(target, patches, options, ct)` | Apply selected patches; respect `DryRun`, `AllowReboot`, `MaxConcurrentMachines`; persist history |
| `VerifyAsync(target, patchIds, ct)` | Re-scan and confirm patch IDs are installed |
| `GetHistoryAsync(machineId, ct)` | Query `IPatchHistoryRepository` for persisted history |
| `GetInstalledAsync(target, ct)` | List all installed patches via remote command |

### 3.5 Detection & Apply Flow

```
DetectAsync(target)
       │
       ▼
PatchStrategyFactory
  .GetStrategy(target.OsType)
       │
       ├── Windows → WindowsPatchStrategy
       │     └─ Execute: Get-WindowsUpdate -MicrosoftUpdate | ConvertTo-Json
       │     └─ Parse: WindowsUpdateParser
       │
       ├── Linux (Debian) → AptPatchStrategy
       │     └─ Execute: apt list --upgradable 2>/dev/null
       │     └─ Parse: AptOutputParser
       │
       └── Linux (RHEL) → YumDnfPatchStrategy
             └─ Execute: dnf check-update --json  (or yum)
             └─ Parse: YumDnfOutputParser
       │
       ▼
  IReadOnlyList<PatchInfo>


ApplyAsync(target, patches, options)
       │
       ▼
   ┌─ DryRun check ──► return simulated PatchResult
   │
   └─ Execute install commands per strategy
       │
       ├─ ForEach patch (batched by MaxConcurrentMachines):
       │     └─ strategy.ApplyAsync(executor, target, batch, options)
       │            │
       │            └─ IRemoteExecutor.ExecuteAsync(target, installCommand)
       │
       ▼
   Parse results → PatchResult
       │
       ├─ Persist each outcome → IPatchHistoryRepository.AddAsync()
       ├─ Emit audit event → IAuditLogger.RecordAsync()
       └─ Return PatchResult to caller
```

### 3.6 Error Handling

| Condition | Behavior |
|---|---|
| Unsupported OS/distro | `NotSupportedException` with detected OS info |
| Remote command failure (nonzero exit) | Map to `PatchOutcome(State: Failed, ErrorMessage)` per patch; aggregate in `PatchResult` |
| Parse failure (unexpected output format) | Log raw output at `Warning`, return partial results with parsing errors noted |
| Reboot required but `AllowReboot = false` | Set `PatchResult.RebootRequired = true` but do not reboot; caller decides |
| Network interruption mid-apply | Circuit-breaker trips; partially-applied state preserved in history |
| Command timeout | Mark timed-out patches as `PatchInstallState.Failed` with timeout message |

### 3.7 Security Considerations

1. **Embedded scripts are read-only resources** — compiled into the assembly; never loaded from user-writable disk paths
2. **Elevation is explicit** — patch commands specify `ElevationMode.Sudo` or `ElevationMode.RunAsAdmin`; no implicit privilege escalation
3. **Patch ID validation** — patch identifiers (KB numbers, package names) are validated against the OS-specific pattern before interpolation into commands
4. **No arbitrary code execution** — the service builds commands from templates; user cannot inject arbitrary shell content through `PatchInfo` fields
5. **Audit trail** — every `ApplyAsync` call records `PatchInstallStarted` → `PatchInstallCompleted`/`PatchInstallFailed`

### 3.8 Logging Strategy

| Level | Events |
|---|---|
| `Information` | Scan started/completed (count of patches found), install started/completed, reboot advisory |
| `Warning` | Partial parse failure, patch apply returned nonzero but some succeeded |
| `Error` | Install failure, unrecognized OS, transport failure during patch operation |
| `Debug` | Raw command output (truncated to 4KB), individual patch install timing, strategy selection reason |

All log entries include: `CorrelationId`, `MachineId`, `Hostname`, `OsType`.

### 3.9 Integration Points

| Subsystem | Direction | Mechanism |
|---|---|---|
| **Remote Execution Engine** | → consumes | All patch commands dispatched via `IRemoteExecutor.ExecuteAsync()` |
| **Machine Inventory** | → consumes | Resolves `MachineTarget` from machine ID for execution context |
| **Audit System** | → produces | Records `PatchScanStarted`, `PatchScanCompleted`, `PatchInstallStarted`, `PatchInstallCompleted`, `PatchInstallFailed` |
| **Orchestration / Job Scheduler** | ← consumed by | Patch scans and applies are submitted as `JobType.PatchScan` / `JobType.PatchApply` |
| **Patch History Repository** | → consumes | Persists `PatchHistoryEntry` records for every apply/verify operation |
| **GUI Backend** | ← consumed by | Patch dashboard displays `DetectAsync` results and history |

---

## 4. Service Controller

**Project:** `HomeManagement.Services`  
**Namespace:** `HomeManagement.Services`  
**Implements:** `IServiceController`

### 4.1 Purpose & Responsibilities

The Service Controller manages **system services (daemons) on remote machines** across Windows and Linux. It:

- Queries individual service status and bulk-lists services with filtering
- Performs start, stop, restart, enable, disable operations
- Supports bulk operations across multiple machines
- Streams service lists for machines with thousands of services
- Uses validated `ServiceName` to prevent injection

### 4.2 Internal Modules & Classes

```
HomeManagement.Services/
├── ServiceModuleRegistration.cs            # IModuleRegistration — self-registers into DI
├── ServiceControllerImpl.cs                # IServiceController implementation — strategy dispatch
├── Strategies/
│   ├── IServiceStrategy.cs                 # Internal strategy interface per OS
│   ├── SystemdServiceStrategy.cs           # Linux: systemctl commands + parsing
│   ├── WindowsServiceStrategy.cs           # Windows: sc.exe / Get-Service PowerShell
│   └── ServiceStrategyFactory.cs           # Selects strategy by OsType
├── Parsers/
│   ├── SystemctlOutputParser.cs            # Parses systemctl list-units / show output
│   └── WindowsServiceParser.cs             # Parses sc.exe / Get-Service JSON output
└── Mappers/
    └── ServiceInfoMapper.cs                # Maps raw output → ServiceInfo/ServiceActionResult
```

### 4.3 Data Structures

```csharp
// Internal strategy interface
internal interface IServiceStrategy
{
    OsType TargetOs { get; }
    Task<ServiceInfo> GetStatusAsync(IRemoteExecutor executor, MachineTarget target, ServiceName name, CancellationToken ct);
    Task<IReadOnlyList<ServiceInfo>> ListAsync(IRemoteExecutor executor, MachineTarget target, ServiceFilter? filter, CancellationToken ct);
    IAsyncEnumerable<ServiceInfo> ListStreamAsync(IRemoteExecutor executor, MachineTarget target, ServiceFilter? filter, CancellationToken ct);
    Task<ServiceActionResult> ControlAsync(IRemoteExecutor executor, MachineTarget target, ServiceName name, ServiceAction action, CancellationToken ct);
}

// Intermediate parse result
internal record RawServiceEntry(
    string Name,
    string DisplayName,
    string StateRaw,
    string StartupTypeRaw,
    int? Pid,
    TimeSpan? Uptime,
    string[] Dependencies);
```

### 4.4 API Surface

Implements `IServiceController` (see [IServiceController.cs](../../src/HomeManagement.Abstractions/Interfaces/IServiceController.cs)):

| Method | Description |
|---|---|
| `GetStatusAsync(target, serviceName, ct)` | Query single service status via OS strategy |
| `ListServicesAsync(target, filter, ct)` | List matching services; null filter = running only |
| `ListServicesStreamAsync(target, filter, ct)` | Stream services via `IAsyncEnumerable<ServiceInfo>` |
| `ControlAsync(target, serviceName, action, ct)` | Execute action; return `ServiceActionResult` |
| `BulkControlAsync(targets, serviceName, action, ct)` | Fan-out across machines with bounded parallelism |

### 4.5 Command Mapping

```
ServiceAction     │ Linux (systemd)              │ Windows (sc.exe / PS)
──────────────────┼──────────────────────────────┼─────────────────────────
Start             │ systemctl start <name>       │ Start-Service <name>
Stop              │ systemctl stop <name>        │ Stop-Service <name> -Force
Restart           │ systemctl restart <name>     │ Restart-Service <name> -Force
Enable            │ systemctl enable <name>      │ Set-Service <name> -StartupType Auto
Disable           │ systemctl disable <name>     │ Set-Service <name> -StartupType Disabled

Status query:     │ systemctl show <name>        │ Get-Service <name> | ConvertTo-Json
                  │ --property=...               │
List:             │ systemctl list-units         │ Get-Service | ConvertTo-Json
                  │ --type=service --all         │
```

### 4.6 Error Handling

| Condition | Behavior |
|---|---|
| Service not found | Return `ServiceActionResult(Success: false, ErrorMessage: "Service not found")` |
| Permission denied | Return failure with `ErrorMessage`; do not retry |
| Timeout during stop | Return `ServiceActionResult(Success: false, ResultingState: Stopping)` |
| BulkControl partial failure | Return results for all targets; caller inspects individual `Success` |
| Unknown service state from output | Map to `ServiceState.Unknown` rather than throw |

### 4.7 Security Considerations

1. **`ServiceName` validated type** — prevents shell metacharacter injection; only `[a-zA-Z0-9\-_.@]` allowed
2. **Elevation requirements** — all service control commands specify `ElevationMode.Sudo` (Linux) or `ElevationMode.RunAsAdmin` (Windows)
3. **No wildcard operations** — `ServiceName` is always a specific service; no glob/regex accepted
4. **Audit trail** — every `ControlAsync` records `ServiceStarted`, `ServiceStopped`, or `ServiceRestarted`
5. **Bulk parallelism bounded** — `BulkControlAsync` uses `SemaphoreSlim` (default: 5 concurrent) to avoid thundering herd

### 4.8 Logging Strategy

| Level | Events |
|---|---|
| `Information` | Service action requested and result (serviceName, action, success/fail, duration) |
| `Warning` | Service not found, action timed out, unexpected state |
| `Error` | Permission denied, transport failure, parse failure |
| `Debug` | Raw `systemctl`/`sc.exe` output, BulkControl parallelism stats |

All log entries include: `CorrelationId`, `MachineId`, `Hostname`, `ServiceName`, `Action`.

### 4.9 Integration Points

| Subsystem | Direction | Mechanism |
|---|---|---|
| **Remote Execution Engine** | → consumes | All service commands dispatched via `IRemoteExecutor.ExecuteAsync()` |
| **Audit System** | → produces | Records `ServiceStarted`, `ServiceStopped`, `ServiceRestarted` |
| **Orchestration** | ← consumed by | Service control submitted as `JobType.ServiceControl` |
| **GUI Backend** | ← consumed by | Service dashboard uses `ListServicesAsync` and `ControlAsync` |
| **Machine Inventory** | → consumes | Resolves machine targets from `MachineId` |

---

## 5. Machine Inventory

**Project:** `HomeManagement.Inventory`  
**Namespace:** `HomeManagement.Inventory`  
**Implements:** `IInventoryService`

### 5.1 Purpose & Responsibilities

The Machine Inventory is the **central registry** of all managed machines. It:

- Maintains machine metadata (OS, hardware, network, tags)
- Supports CRUD with soft-delete semantics
- Refreshes metadata via remote execution (re-detect OS, hardware, connectivity)
- Discovers machines on a network range (CIDR scan)
- Provides import/export for bulk operations
- Supports paged, filtered querying

### 5.2 Internal Modules & Classes

```
HomeManagement.Inventory/
├── InventoryModuleRegistration.cs          # IModuleRegistration — self-registers into DI
├── InventoryServiceImpl.cs                 # IInventoryService implementation
├── Discovery/
│   ├── NetworkScanner.cs                   # CIDR range scanner — ping sweep + port probe
│   └── MachineProber.cs                    # Probe a single host — detect OS, gather metadata
├── Metadata/
│   ├── IMetadataCollector.cs               # Internal interface for OS-specific metadata
│   ├── LinuxMetadataCollector.cs           # uname, lscpu, free, lsblk commands
│   ├── WindowsMetadataCollector.cs         # Get-CimInstance, systeminfo commands
│   └── MetadataCollectorFactory.cs         # Select collector by OsType
├── Import/
│   ├── CsvMachineImporter.cs              # Parse CSV → MachineCreateRequest[]
│   └── CsvMachineExporter.cs              # Machine[] → CSV stream
└── Mappers/
    └── MachineMapper.cs                    # MachineEntity ↔ Machine DTO mapping
```

### 5.3 Data Structures

```csharp
// Internal metadata collector interface
internal interface IMetadataCollector
{
    OsType TargetOs { get; }
    Task<MetadataSnapshot> CollectAsync(IRemoteExecutor executor, MachineTarget target, CancellationToken ct);
}

// Intermediate metadata result
internal record MetadataSnapshot(
    string OsVersion,
    int CpuCores,
    long RamBytes,
    DiskInfo[] Disks,
    string Architecture,
    System.Net.IPAddress[] DetectedIpAddresses);

// Network scan intermediate result
internal record ScanResult(
    System.Net.IPAddress Address,
    bool Reachable,
    int? OpenPort,
    TimeSpan Latency);

// CSV import intermediate
internal record CsvMachineRow(
    string Hostname,
    string? Fqdn,
    string OsType,
    string ConnectionMode,
    string Protocol,
    int Port,
    string CredentialLabel,
    string? Tags);
```

### 5.4 API Surface

Implements `IInventoryService` (see [IInventoryService.cs](../../src/HomeManagement.Abstractions/Interfaces/IInventoryService.cs)):

| Method | Description |
|---|---|
| `AddAsync(request, ct)` | Validate, test connectivity (optional), persist via `IMachineRepository`, emit audit event |
| `UpdateAsync(id, request, ct)` | Partial update; merge tags; persist; emit audit event |
| `RemoveAsync(id, ct)` | Soft-delete via `IMachineRepository.SoftDeleteAsync()`; audit trail preserved |
| `GetAsync(id, ct)` | Retrieve by ID; returns `null` if not found or soft-deleted |
| `QueryAsync(query, ct)` | Paged, filtered query; respects `IncludeDeleted` flag |
| `RefreshMetadataAsync(id, ct)` | Connect to machine → collect metadata → update record |
| `DiscoverAsync(range, ct)` | CIDR scan → probe reachable hosts → return discovered `Machine[]` |
| `ImportAsync(csvStream, ct)` | Parse CSV → validate → bulk add |
| `ExportAsync(query, destination, format, ct)` | Query → serialize to CSV or JSON stream |

### 5.5 Discovery Flow

```
DiscoverAsync(CidrRange "192.168.1.0/24")
       │
       ▼
  NetworkScanner
    .ScanRangeAsync("192.168.1.0/24")
       │
       ├── Parallel ping sweep (bounded: 20 concurrent)
       │    └── ICMP echo + TCP port probe (22, 5985, 5986)
       │
       ▼
  ScanResult[] (reachable hosts)
       │
       ▼
  MachineProber.ProbeAsync(ip, port)
       │
       ├── IRemoteExecutor.TestConnectionAsync() → ConnectionTestResult
       │     (detects OsType, OsVersion, protocol)
       │
       ├── If reachable: MetadataCollectorFactory → IMetadataCollector
       │     └── Collect CPU, RAM, disk, architecture
       │
       ▼
  Machine[] (unenrolled — returned to caller)
  Caller decides whether to AddAsync() each discovered machine
```

### 5.6 Error Handling

| Condition | Behavior |
|---|---|
| Duplicate hostname | `InvalidOperationException("Hostname already registered")` |
| Machine not found | Return `null` from `GetAsync`; throw from `UpdateAsync`/`RemoveAsync` |
| Credential not found | Validate `CredentialId` exists in vault before persisting |
| Network scan host unreachable | Include in scan results as unreachable; do not throw |
| CSV parse error | Collect all row errors; throw `AggregateException` with row numbers |
| Metadata collection partial failure | Update fields that succeeded; log warnings for failed fields |
| CIDR range too large (>/16) | Reject immediately — prevent accidental scan of 65k+ hosts |

### 5.7 Security Considerations

1. **`Hostname` validated type** — prevents injection through machine names
2. **`CidrRange` validated type** — prevents malformed network ranges; capped at /16 maximum
3. **Soft-delete only** — hard delete is never exposed; audit trail is preserved
4. **Tag values sanitized** — tag keys/values are trimmed and length-limited (256 chars) before storage
5. **CSV import validation** — every row is validated before any database writes; no partial imports
6. **Discovery is read-only** — `DiscoverAsync` returns candidates but does not modify the database
7. **Credential existence check** — `AddAsync` and `UpdateAsync` verify the referenced credential exists in the vault

### 5.8 Logging Strategy

| Level | Events |
|---|---|
| `Information` | Machine added/updated/soft-deleted, metadata refreshed, discovery completed (host count), import/export completed |
| `Warning` | Duplicate hostname on add, credential not found, discovery host unreachable, CSV row validation failure |
| `Error` | Database persistence failure, metadata collection failure, discovery scan error |
| `Debug` | Individual host scan timing, metadata command output, tag merge details, CSV row count |

All log entries include: `CorrelationId`, `MachineId` (when applicable), `Hostname`.

### 5.9 Integration Points

| Subsystem | Direction | Mechanism |
|---|---|---|
| **Machine Repository (Data)** | → consumes | All CRUD operations via `IMachineRepository` |
| **Remote Execution Engine** | → consumes | `TestConnectionAsync`, `ExecuteAsync` for metadata collection and discovery |
| **Credential Vault** | → consumes | Validate credential existence; resolve credentials for connectivity tests |
| **Audit System** | → produces | Records `MachineAdded`, `MachineRemoved`, `MachineMetadataRefreshed` |
| **Patch Manager** | ← consumed by | Provides `MachineTarget` for patch operations |
| **Service Controller** | ← consumed by | Provides `MachineTarget` for service operations |
| **Orchestration** | ← consumed by | Metadata refresh submitted as `JobType.MetadataRefresh` |
| **GUI Backend** | ← consumed by | Machine list, detail, tag management |

---

## 6. Logging / Audit System

**Project:** `HomeManagement.Auditing`  
**Namespace:** `HomeManagement.Auditing`  
**Implements:** `IAuditLogger`

### 6.1 Purpose & Responsibilities

The Audit System provides a **tamper-evident, structured record** of every action performed in the HomeManagement system. It:

- Records audit events with correlation, actor identity, target machine, and outcome
- Maintains an HMAC chain for tamper detection (each event hash includes the previous event's hash)
- Supports paged querying with rich filters
- Exports audit trails for compliance reporting
- Provides counting for dashboards
- Integrates with `ISensitiveDataFilter` to prevent credential leakage in audit detail fields

### 6.2 Internal Modules & Classes

```
HomeManagement.Auditing/
├── AuditModuleRegistration.cs             # IModuleRegistration — self-registers into DI
├── AuditLoggerImpl.cs                     # IAuditLogger implementation — orchestrates record/query
├── Chain/
│   ├── HmacChainProvider.cs               # Computes HMAC-SHA256 chain: hash(event + previousHash)
│   └── ChainVerifier.cs                   # Verifies audit chain integrity on demand
├── Export/
│   ├── AuditCsvExporter.cs                # AuditEvent[] → CSV stream
│   └── AuditJsonExporter.cs               # AuditEvent[] → JSON stream
├── Enrichment/
│   └── AuditEventEnricher.cs              # Stamps CorrelationId, ActorIdentity, redacts sensitive fields
└── Mappers/
    └── AuditEventMapper.cs                # AuditEventEntity ↔ AuditEvent DTO mapping
```

### 6.3 Data Structures

```csharp
// HMAC chain computation context
internal record ChainLink(
    Guid EventId,
    DateTime TimestampUtc,
    string CanonicalPayload,   // Deterministic serialization of the event for hashing
    string PreviousHash,       // Hash of the preceding event (empty string for first event)
    string EventHash);         // HMAC-SHA256(CanonicalPayload + PreviousHash, chainKey)

// Chain verification result
internal record ChainVerificationResult(
    bool IsValid,
    int EventsVerified,
    Guid? FirstBrokenEventId,
    string? ErrorMessage);

// Export format handler interface
internal interface IAuditExporter
{
    ExportFormat Format { get; }
    Task ExportAsync(IAsyncEnumerable<AuditEvent> events, Stream destination, CancellationToken ct);
}
```

### 6.4 API Surface

Implements `IAuditLogger` (see [IAuditLogger.cs](../../src/HomeManagement.Abstractions/Interfaces/IAuditLogger.cs)):

| Method | Description |
|---|---|
| `RecordAsync(auditEvent, ct)` | Enrich → compute HMAC chain link → persist via `IAuditEventRepository` |
| `QueryAsync(query, ct)` | Delegate to `IAuditEventRepository.QueryAsync()` with mapping |
| `CountAsync(query, ct)` | Delegate to `IAuditEventRepository.CountAsync()` |
| `ExportAsync(query, destination, format, ct)` | Stream matching events through the appropriate exporter |

### 6.5 HMAC Chain Mechanism

```
Event₁:
  CanonicalPayload = JSON(EventId, TimestampUtc, Action, ActorIdentity, TargetMachineId, Outcome)
  PreviousHash = ""  (first event)
  EventHash = HMAC-SHA256(CanonicalPayload + "", chainKey)

Event₂:
  CanonicalPayload = JSON(...)
  PreviousHash = Event₁.EventHash
  EventHash = HMAC-SHA256(CanonicalPayload + Event₁.EventHash, chainKey)

Event₃:
  PreviousHash = Event₂.EventHash
  EventHash = HMAC-SHA256(CanonicalPayload + Event₂.EventHash, chainKey)
  ...

Verification:
  For each event in sequence:
    Recompute hash from canonical payload + previous hash
    Compare with stored EventHash
    If mismatch → chain broken at this event → alert
```

### 6.6 Record Flow

```
RecordAsync(auditEvent)
       │
       ▼
  AuditEventEnricher
    .Enrich(event)
       │
       ├── Stamp CorrelationId from ICorrelationContext
       ├── Stamp ActorIdentity (from ambient context or explicit)
       └── Redact Detail via ISensitiveDataFilter
       │
       ▼
  HmacChainProvider
    .ComputeLink(enrichedEvent)
       │
       ├── IAuditEventRepository.GetLastEventHashAsync() → previousHash
       ├── Serialize event to canonical JSON
       └── HMAC-SHA256(canonical + previousHash, chainKey)
       │
       ▼
  IAuditEventRepository
    .AddAsync(event, previousHash, eventHash)
       │
       ▼
  Serilog structured log (separate from audit persistence)
```

### 6.7 Error Handling

| Condition | Behavior |
|---|---|
| Database write failure | Retry once (transient); if persistent, log to Serilog as `Fatal` — audit data must not be silently lost |
| HMAC chain inconsistency | Log `Critical` alert; continue recording (do not block operations) |
| Export stream interrupted | Close stream gracefully; caller receives partial export with error |
| Query filter returns no results | Return `PagedResult<AuditEvent>([], 0, page, pageSize)` — not an error |
| Concurrent RecordAsync calls | `SemaphoreSlim(1)` serializes chain computation to maintain hash order |

### 6.8 Security Considerations

1. **HMAC chain key management** — chain key is derived from a separate secret stored in the vault (not the master password); rotatable with re-hash of existing chain
2. **Canonical serialization** — deterministic JSON (sorted keys, no whitespace, UTC dates) ensures hash reproducibility
3. **Sensitive data filtering** — `ISensitiveDataFilter.Redact()` applied to `Detail` and `Properties` before persistence; passwords, tokens, keys are never stored in audit logs
4. **Append-only semantics** — no `UpdateAsync` or `DeleteAsync` on audit events; records are immutable once written
5. **Chain verification on demand** — admin can trigger `ChainVerifier.VerifyAsync()` to detect tampering
6. **Actor identity from ambient context** — cannot be spoofed by callers; derived from authenticated session

### 6.9 Logging Strategy

This subsystem has two distinct logging channels:

1. **Audit persistence** (primary purpose) — writes to SQLite via EF Core
2. **Operational logging** (meta-logging via Serilog) — logs about the audit system itself

| Level | Events (operational) |
|---|---|
| `Information` | Audit event recorded (action, outcome — not details), export completed |
| `Warning` | Chain hash computation slower than 100ms, export stream stalled |
| `Error` | Database write failure, chain integrity mismatch detected |
| `Fatal` | Unable to persist audit event after retry — data loss risk |
| `Debug` | Chain link details, query execution time, export row count |

### 6.10 Integration Points

| Subsystem | Direction | Mechanism |
|---|---|---|
| **All subsystems** | ← consumed by | Every subsystem calls `IAuditLogger.RecordAsync()` for auditable actions |
| **Audit Event Repository (Data)** | → consumes | Persistence via `IAuditEventRepository` |
| **Credential Vault** | → consumes | Retrieves HMAC chain key for hash computation |
| **ICorrelationContext** | → consumes | Stamps correlation ID on every audit event |
| **ISensitiveDataFilter** | → consumes | Redacts sensitive data before persistence |
| **GUI Backend** | ← consumed by | Audit trail viewer, compliance reporting, dashboard counts |

---

## 7. GUI Backend

**Project:** `HomeManagement.Gui`  
**Namespace:** `HomeManagement.Gui`  
**Framework:** Avalonia 11 + ReactiveUI (MVVM)

### 7.1 Purpose & Responsibilities

The GUI Backend is the **desktop application shell** that wires all subsystems into a user-facing interface. It:

- Bootstraps the DI container and discovers module registrations
- Provides MVVM ViewModels that mediate between the UI and domain services
- Handles navigation between views
- Manages application lifecycle (startup, shutdown, vault lock on idle)
- Surfaces real-time job progress and agent events via reactive subscriptions
- Presents system health status

### 7.2 Internal Modules & Classes

```
HomeManagement.Gui/
├── Program.cs                              # Entry point — Avalonia AppBuilder
├── App.axaml.cs                            # Application lifecycle — DI bootstrap, navigation setup
├── App.axaml                               # Avalonia application XAML (themes, resources)
├── Startup/
│   ├── AppBootstrapper.cs                  # Service collection build, module discovery, DB init
│   └── NavigationService.cs                # IScreen-based navigation via ReactiveUI routing
├── ViewModels/
│   ├── MainWindowViewModel.cs              # Root ViewModel — hosts navigation, toolbar, status bar
│   ├── Dashboard/
│   │   └── DashboardViewModel.cs           # Overview: machine count, recent jobs, health status
│   ├── Machines/
│   │   ├── MachineListViewModel.cs         # Paged machine list with filtering/search
│   │   ├── MachineDetailViewModel.cs       # Single machine: metadata, patch history, services
│   │   └── MachineDiscoveryViewModel.cs    # Network discovery wizard
│   ├── Patching/
│   │   ├── PatchScanViewModel.cs           # Scan machines for patches, review results
│   │   └── PatchApplyViewModel.cs          # Select patches, configure options, apply
│   ├── Services/
│   │   └── ServiceManagerViewModel.cs      # List/control services across machines
│   ├── Jobs/
│   │   ├── JobListViewModel.cs             # Job history with filtering
│   │   └── JobDetailViewModel.cs           # Single job: progress, per-machine results
│   ├── Credentials/
│   │   ├── VaultUnlockViewModel.cs         # Master password prompt
│   │   └── CredentialManagerViewModel.cs   # CRUD credential entries
│   ├── Audit/
│   │   └── AuditTrailViewModel.cs          # Audit event browser with advanced filtering
│   └── Settings/
│       └── SettingsViewModel.cs            # Application settings, vault management
├── Views/
│   ├── MainWindow.axaml(.cs)               # Root window — NavigationView + ContentArea
│   ├── Dashboard/
│   │   └── DashboardView.axaml(.cs)
│   ├── Machines/
│   │   ├── MachineListView.axaml(.cs)
│   │   ├── MachineDetailView.axaml(.cs)
│   │   └── MachineDiscoveryView.axaml(.cs)
│   ├── Patching/
│   │   ├── PatchScanView.axaml(.cs)
│   │   └── PatchApplyView.axaml(.cs)
│   ├── Services/
│   │   └── ServiceManagerView.axaml(.cs)
│   ├── Jobs/
│   │   ├── JobListView.axaml(.cs)
│   │   └── JobDetailView.axaml(.cs)
│   ├── Credentials/
│   │   ├── VaultUnlockView.axaml(.cs)
│   │   └── CredentialManagerView.axaml(.cs)
│   ├── Audit/
│   │   └── AuditTrailView.axaml(.cs)
│   └── Settings/
│       └── SettingsView.axaml(.cs)
├── Controls/
│   ├── StatusBarControl.axaml(.cs)         # Health, vault lock status, active job indicator
│   ├── MachinePickerControl.axaml(.cs)     # Reusable machine multi-selector
│   └── ProgressOverlayControl.axaml(.cs)   # Overlay shown during long operations
├── Converters/
│   ├── StateToColorConverter.cs            # MachineState/ServiceState → color
│   ├── SeverityToIconConverter.cs          # PatchSeverity → icon
│   └── BoolToVisibilityConverter.cs
└── Services/
    ├── IDialogService.cs                   # Show message boxes, confirmation dialogs
    ├── DialogService.cs
    ├── IClipboardService.cs                # Cross-platform clipboard access
    └── ClipboardService.cs
```

### 7.3 Data Structures

```csharp
// Navigation page identifiers
internal enum NavigationPage
{
    Dashboard,
    MachineList,
    MachineDetail,
    MachineDiscovery,
    PatchScan,
    PatchApply,
    ServiceManager,
    JobList,
    JobDetail,
    CredentialManager,
    AuditTrail,
    Settings
}

// Navigation request
internal record NavigationRequest(
    NavigationPage Page,
    object? Parameter = null);  // e.g., MachineId for MachineDetail

// Status bar state
internal record AppStatusInfo(
    bool VaultUnlocked,
    int ConnectedAgents,
    int RunningJobs,
    HealthStatus OverallHealth);

// ViewModel base class
public abstract class ViewModelBase : ReactiveObject, IRoutableViewModel
{
    public abstract string UrlPathSegment { get; }
    public IScreen HostScreen { get; }
}
```

### 7.4 API Surface

The GUI Backend does not expose a public API — it **consumes** all other subsystem interfaces via DI. Its "API surface" is the set of ViewModels bound to Views:

| ViewModel | Domain Services Consumed | Responsibilities |
|---|---|---|
| `DashboardViewModel` | `IInventoryService`, `IJobScheduler`, `ISystemHealthService` | Machine count, recent jobs, health |
| `MachineListViewModel` | `IInventoryService` | Paged list, search, filter, soft-delete |
| `MachineDetailViewModel` | `IInventoryService`, `IPatchService`, `IServiceController` | Detail view, patch history, service list |
| `MachineDiscoveryViewModel` | `IInventoryService` | CIDR input, scan progress, add discovered |
| `PatchScanViewModel` | `IPatchService`, `IInventoryService`, `ICommandBroker` | Select targets, scan, display results; subscribes to `CompletedStream` for async status feedback |
| `PatchApplyViewModel` | `IPatchService`, `IJobScheduler`, `ICommandBroker` | Configure options, submit as job; dispatches through broker |
| `ServiceManagerViewModel` | `IServiceController`, `IInventoryService`, `ICommandBroker` | List services, perform actions; start/stop/restart dispatched via `ICommandBroker.SubmitAsync()`; subscribes to `CompletedStream` for auto-refresh |
| `JobListViewModel` | `IJobScheduler` | Paged job list, filter by type/state |
| `JobDetailViewModel` | `IJobScheduler` | Real-time progress via `ProgressStream`, per-machine results |
| `VaultUnlockViewModel` | `ICredentialVault` | Master password prompt, unlock/lock |
| `CredentialManagerViewModel` | `ICredentialVault` | CRUD credentials, rotation |
| `AuditTrailViewModel` | `IAuditLogger` | Query, filter, export audit events |
| `SettingsViewModel` | `ICredentialVault`, `ISystemHealthService` | Vault management, system health check |

### 7.5 Application Bootstrap Flow

```
Program.Main()
       │
       ▼
  AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseReactiveUI()
       │
       ▼
  App.OnFrameworkInitializationCompleted()
       │
       ▼
  AppBootstrapper.BuildServiceProvider()
       │
       ├── ServiceCollection sc = new()
       ├── sc.AddHomeManagementLogging(dataDir)
       ├── sc.AddHomeManagement(dataDir, moduleAssemblies)
       │     └── Discovers IModuleRegistration in:
       │           Vault, Transport, Patching, Services,
       │           Inventory, Auditing, Orchestration
       ├── sc.AddSingleton<NavigationService>()
       ├── sc.AddTransient<*ViewModel>() for each ViewModel
       └── BuildServiceProvider()
       │
       ▼
  ServiceRegistration.InitializeDatabaseAsync()
       │
       ▼
  new MainWindow { DataContext = Resolve<MainWindowViewModel>() }
       │
       ▼
  Resolve<IAgentGateway>().StartAsync()   (listen for agent connections)
       │
       ▼
  Resolve<CommandBrokerService>().Start() (begin draining command queue)
       │
       ▼
  NavigationService.NavigateTo(Dashboard)
```

### 7.6 Reactive Subscriptions

```csharp
// JobDetailViewModel — real-time progress
IJobScheduler.GetJobProgressStream(jobId)
    .ObserveOn(RxApp.MainThreadScheduler)  // Marshal to UI thread
    .Subscribe(evt => {
        ProgressPercent = evt.ProgressPercent;
        CurrentMachine = evt.MachineName;
        StatusMessage = evt.Message;
    });

// MainWindowViewModel — agent connection events
IAgentGateway.ConnectionEvents
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(evt => {
        ConnectedAgentCount = AgentGateway.GetConnectedAgents().Count;
    });

// StatusBar — vault lock state polling
Observable.Interval(TimeSpan.FromSeconds(5))
    .Select(_ => CredentialVault.IsUnlocked)
    .DistinctUntilChanged()
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(unlocked => VaultUnlocked = unlocked);

// ServicesViewModel / PatchingViewModel — fire-and-forget command dispatch + auto-refresh
// Dispatch a service control action through the async command broker
var trackingId = await _broker.SubmitAsync(new CommandEnvelope(
    Target: machineTarget,
    Command: BuildControlCommand(serviceName, action)));
StatusMessage = $"Dispatched {action} (tracking: {trackingId:N})";

// Subscribe to CompletedStream for auto-refresh when results arrive
_broker.CompletedStream
    .Where(e => e.MachineId == SelectedMachine?.Id)
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(async evt => {
        await RefreshServicesAsync();
        StatusMessage = evt.Result.ExitCode == 0
            ? $"{action} completed successfully"
            : $"{action} failed: {evt.Result.Stderr}";
    });
```

### 7.7 Error Handling

| Condition | Behavior |
|---|---|
| Vault locked when operation requires credentials | Navigate to `VaultUnlockViewModel`; return to original view after unlock |
| Service call throws | ViewModel catches, sets `ErrorMessage` property, shows error in UI |
| Network timeout | Show timeout message; offer retry via `ReactiveCommand.CreateFromTask` |
| Job submission failure | Show inline error; do not navigate away from form |
| Background subscription error | `OnError` handler logs to Serilog + shows notification |
| Database migration failure at startup | Show fatal error dialog; exit application |

### 7.8 Security Considerations

1. **No credentials in ViewModel properties** — ViewModels never hold decrypted credentials; `VaultUnlockViewModel` passes `SecureString` directly to `ICredentialVault.UnlockAsync()` and dereferences it
2. **Auto-lock on idle** — monitor user input; lock vault after configurable idle timeout (default: 15 minutes)
3. **No sensitive data in clipboard** — `IClipboardService` auto-clears after 30 seconds when used for credential data
4. **UI thread isolation** — all `IObservable` subscriptions marshal to main thread via `ObserveOn(RxApp.MainThreadScheduler)`
5. **Input validation in ViewModels** — use `Hostname.TryCreate()`, `ServiceName.TryCreate()`, `CidrRange.TryCreate()` before calling services
6. **Audit on user actions** — every user-initiated operation (not just reads) emits an audit event via `IAuditLogger`

### 7.9 Logging Strategy

| Level | Events |
|---|---|
| `Information` | Navigation transitions, user-initiated commands (scan, apply, control), application startup/shutdown |
| `Warning` | Auto-lock triggered, slow VM operation (>2s), subscription error recovered |
| `Error` | Unhandled ViewModel exception, service call failure, database migration failure |
| `Debug` | ViewModel lifecycle (construct/dispose), reactive subscription management, binding diagnostics |

### 7.10 Integration Points

| Subsystem | Direction | Mechanism |
|---|---|---|
| **All domain services** | → consumes | Every subsystem is injected into corresponding ViewModels via DI |
| **Core (composition root)** | → consumes | `AddHomeManagement()` + `AddHomeManagementLogging()` called in `AppBootstrapper` |
| **ICorrelationContext** | → consumes | Each user action begins a new correlation scope |
| **ISystemHealthService** | → consumes | Dashboard and status bar show health status |
| **NavigationService** | internal | Coordinates ViewModel-to-ViewModel transitions via ReactiveUI routing |

---

## Cross-Subsystem Dependency Matrix

```
                     Vault  Transport  Patching  Services  Inventory  Auditing  GUI/Orch
Credential Vault      ──      ←           ←         ←         ←          ←         ←
Remote Execution      →       ──          ←         ←         ←          .         ←
Patch Manager         →       →           ──        .         →          →         ←
Service Controller    →       →           .         ──        →          →         ←
Machine Inventory     →       →           .         .         ──         →         ←
Audit System          →       .           .         .         .          ──        ←
GUI / Orchestration   →       →           →         →         →          →         ──

Legend:  → = depends on (consumes)   ← = depended on by   . = no direct dependency
```

## Shared Patterns Across All Subsystems

### Module Self-Registration

Every subsystem implements `IModuleRegistration`:

```csharp
internal sealed class XxxModuleRegistration : IModuleRegistration
{
    public string ModuleName => "HomeManagement.Xxx";

    public void Register(IServiceCollection services)
    {
        services.AddScoped<IXxxService, XxxServiceImpl>();
        // Register any internal strategies, parsers, etc.
    }
}
```

### Correlation Propagation

Every public method on every service implementation:

```csharp
public async Task<T> SomeOperationAsync(..., CancellationToken ct)
{
    using var scope = _correlation.BeginScope();
    _logger.LogInformation("Operation started. CorrelationId={CorrelationId}", _correlation.CorrelationId);
    // ... operation logic ...
}
```

### Audit Event Emission

Every state-changing operation:

```csharp
await _auditLogger.RecordAsync(new AuditEvent(
    EventId: Guid.NewGuid(),
    TimestampUtc: DateTime.UtcNow,
    CorrelationId: _correlation.CorrelationId,
    Action: AuditAction.XxxCompleted,
    ActorIdentity: "system",  // or user identity from ambient context
    TargetMachineId: machineId,
    TargetMachineName: hostname.Value,
    Detail: "Description of what happened",
    Properties: null,
    Outcome: AuditOutcome.Success,
    ErrorMessage: null), ct);
```

### Resilience Wrapping (Transport-bound operations)

```csharp
var result = await _resilience.ExecuteAsync(
    targetKey: $"{target.Hostname}:{target.Port}",
    action: async ct => await _executor.ExecuteAsync(target, command, ct),
    ct);
```
