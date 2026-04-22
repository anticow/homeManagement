# 11 — Agent Architecture

> Lightweight cross-platform agent for Windows, Linux, and macOS managed machines.
> Supersedes agent-related fragments in docs 02, 05, and 08.
>
> **Revision 2 — 2026-03-21:** Control-plane topology aligned to platform architecture.
> Agents connect to the standalone AgentGateway service, not the desktop GUI. Broker remains the system of record for commands and execution state.

---

## 1  Overview

The **hm-agent** is a self-contained, single-binary service (~10 MB) that runs on
each managed machine in **agent mode** (`MachineConnectionMode.Agent`).
It initiates an **outbound** gRPC connection to the standalone **AgentGateway** service,
removing every requirement for inbound firewall rules on the target.

```
┌──────────────────────────────────────┐
│      Platform Control Plane         │
│  ┌────────────────────────────────┐  │
│  │ AgentGateway (gRPC edge)       │  │
│  │ :9444 — mTLS, bidirectional    │  │
│  └──────────────┬─────────────────┘  │
│                 │                     │
└─────────────────┼─────────────────────┘
                  │  outbound from agent
    ┌─────────────┴────────────────────┐
    │                                  │
┌───▼──────────┐          ┌────────────▼──┐
│  hm-agent    │          │  hm-agent     │
│  Linux host  │          │  Windows host │
│  systemd     │          │  Win Service  │
└──────────────┘          └───────────────┘
          \                    /
           \                  /
            \                /
             ┌──────────────┐
             │   hm-agent   │
             │  macOS host  │
             │   launchd    │
             └──────────────┘
```

### Design goals

| # | Goal | Mechanism |
|---|------|-----------|
| G1 | **Minimal footprint** | Single binary, no runtime dependencies, < 50 MB RSS |
| G2 | **Firewall-friendly** | Agent opens outbound TCP only (port 9444 directly or 443 through ingress) |
| G3 | **Secure by default** | mTLS with private CA; no passwords in config |
| G4 | **Self-updating** | Controller pushes update packages; agent verifies, installs, restarts |
| G5 | **Resilient** | Exponential-backoff reconnect; command timeout; local log buffer |
| G6 | **Observable** | Structured JSON logs; heartbeat telemetry; audit correlation IDs |

The agent does not know about GUI or Broker internals. It speaks only to AgentGateway over the gRPC protocol.

---

## 2  Internal Architecture

```
hm-agent process
├── Hosting
│   ├── AgentHostService           : BackgroundService — bootstraps gRPC, schedules heartbeats
│   ├── SystemdLifetime            : IHostLifetime  (Linux)
│   ├── WindowsServiceLifetime     : IHostLifetime  (Windows)
│   └── ConsoleLifetime + launchd  : Host lifetime wrapper (macOS)
│
├── Communication
│   ├── GrpcChannelManager         : Owns the gRPC channel + mTLS credentials
│   ├── AgentGrpcClient            : Generated client stub → AgentHub.proto
│   └── CommandDispatcher          : Routes inbound CommandRequest → handler
│
├── Handlers (one per command domain)
│   ├── ShellCommandHandler        : Runs OS processes, captures stdout/stderr
│   ├── PatchCommandHandler        : Detects / applies patches (delegates to OS APIs or scripts)
│   ├── ServiceCommandHandler      : Start / stop / restart / status (systemd / SC / launchctl)
│   ├── SystemInfoHandler          : Hardware + OS metadata collection
│   └── UpdateCommandHandler       : Self-update lifecycle
│
├── Security
│   ├── CertificateLoader          : Loads agent.pfx + CA trust anchors, validates client/server chains
│   ├── CommandValidator           : Allowlist, rate limiter, elevation guard
│   └── IntegrityChecker           : SHA-256 / Ed25519 verification for update packages
│
├── Resilience
│   ├── ReconnectPolicy            : Exponential backoff (1 s → 5 min cap)
│   └── CommandTimeoutEnforcer     : Per-command CancellationTokenSource + kill
│
└── Configuration
    ├── AgentConfiguration         : Typed POCO bound from hm-agent.json
    └── AgentPlatformDetector      : Detects OS, architecture, init system
```

### 2.1  Class responsibilities

| Class | Responsibility |
|-------|---------------|
| `AgentHostService` | `IHostedService`. On start: load config → load certs → open gRPC channel to AgentGateway → enter command loop. Inbound `Channel<CommandRequest>` (bounded capacity 128) decouples the gRPC receive loop from command execution; `CommandProcessingLoopAsync` drains the queue according to the configured concurrency policy. On stop: drain pending commands → close channel. |
| `GrpcChannelManager` | Creates `GrpcChannel` with `SslCredentials`; exposes `GetChannel()`. Recreates channel on reconnect. |
| `CommandDispatcher` | Receives `CommandRequest` from gRPC stream → resolves `ICommandHandler` by `CommandType` enum → calls `HandleAsync` → returns `CommandResponse`. |
| `ShellCommandHandler` | Spawns `System.Diagnostics.Process` with redirected I/O. Applies `ElevationMode`. Enforces per-command `Timeout`. |
| `PatchCommandHandler` | Delegates to `PatchStrategy` (Windows Update API on Windows, `apt`/`yum`/`dnf` on Linux, `softwareupdate` plus optional Homebrew on macOS). Returns `PatchResult` JSON. |
| `ServiceCommandHandler` | Delegates to `ServiceStrategy` (`systemctl` on Linux, `sc.exe` / `Get-Service` on Windows, `launchctl` on macOS). Returns `ServiceActionResult` JSON. |
| `SystemInfoHandler` | Reads `/proc`, WMI, registry, `uname`, `sysctl`, and `diskutil` data. Returns `AgentMetadata`-shaped JSON. |
| `UpdateCommandHandler` | Downloads binary → verifies SHA-256 + Ed25519 signature → stages in temp dir → signals `AgentHostService` to restart with new binary. |
| `CertificateLoader` | Loads `X509Certificate2` from PFX; validates the agent certificate against the client CA and the server certificate against either `serverCaCertPath` or the fallback client CA; rejects expired/revoked certs. |
| `CommandValidator` | Enforces: command allowlist, max 10 commands/sec sliding window, elevation requires explicit flag in request. |
| `IntegrityChecker` | Verifies `AgentUpdatePackage.BinarySha256` against downloaded file; optionally checks Ed25519 detached signature. |
| `ReconnectPolicy` | On channel fault: sleep `min(baseDelay × 2^attempt, maxDelay)` with ±20 % jitter; reset on successful handshake. |
| `CommandTimeoutEnforcer` | Wraps each command execution in `CancellationTokenSource(timeout)`; kills the spawned process if it exceeds the deadline. |
| `AgentPlatformDetector` | Returns `OsType`, architecture, init system (systemd / Windows Service / launchd), package manager. |

---

## 3  gRPC Service Definition

The agent uses a **bidirectional streaming** RPC. The agent opens the stream;
the controller sends commands down it; the agent sends responses back.
A dedicated heartbeat message keeps the connection alive and delivers telemetry.

```protobuf
syntax = "proto3";

package homemanagement.agent.v1;

option csharp_namespace = "HomeManagement.Agent.Protocol";

// ──────────────────────────────────────────────
//  The single bidirectional service
// ──────────────────────────────────────────────
service AgentHub {
  // Agent opens this stream on startup.
  // AgentGateway writes CommandRequest; agent writes AgentMessage.
  rpc Connect (stream AgentMessage) returns (stream ControlMessage);
}

// ──────────────────────────────────────────────
//  Agent → AgentGateway
// ──────────────────────────────────────────────
message AgentMessage {
  oneof payload {
    Handshake       handshake        = 1;
    Heartbeat       heartbeat        = 2;
    CommandResponse command_response = 3;
  }
}

message Handshake {
  string agent_id        = 1;
  string hostname        = 2;
  string agent_version   = 3;
  string os_type         = 4;  // "Windows" | "Linux"
  string os_version      = 5;
  string architecture    = 6;  // "x64" | "arm64"
}

message Heartbeat {
  string agent_id          = 1;
  int64  uptime_seconds    = 2;
  double cpu_percent       = 3;
  int64  memory_used_bytes = 4;
  int64  memory_total_bytes= 5;
  int64  disk_free_bytes   = 6;
  string timestamp_utc     = 7;  // ISO 8601
}

message CommandResponse {
  string request_id     = 1;
  int32  exit_code      = 2;
  string stdout         = 3;
  string stderr         = 4;
  int64  duration_ms    = 5;
  bool   timed_out      = 6;
  string result_json    = 7;  // structured payload for typed commands
  string error_category = 8;  // maps to ErrorCategory enum
}

// ──────────────────────────────────────────────
//  AgentGateway → Agent
// ──────────────────────────────────────────────
message ControlMessage {
  oneof payload {
    CommandRequest  command_request  = 1;
    UpdateDirective update_directive = 2;
    Shutdown        shutdown         = 3;
    Ack             ack              = 4;  // controller acknowledges handshake / heartbeat
  }
}

message CommandRequest {
  string request_id       = 1;  // UUID — correlates response, audit
  string command_type     = 2;  // "Shell" | "PatchScan" | "PatchApply" | "ServiceControl" | "SystemInfo"
  string command_text     = 3;  // raw command (Shell only)
  string parameters_json  = 4;  // typed parameters for PatchScan / ServiceControl / etc.
  int32  timeout_seconds  = 5;
  string elevation_mode   = 6;  // "None" | "Sudo" | "SudoAsUser" | "RunAsAdmin"
  string run_as_user      = 7;  // optional elevation target
  map<string, string> env = 8;  // environment variable overrides
  string correlation_id   = 9;  // propagated through audit chain
}

message UpdateDirective {
  string target_version    = 1;
  string download_url      = 2;  // HTTPS URL on control machine's update server
  bytes  binary_sha256     = 3;  // 32 bytes
  bytes  signature_ed25519 = 4;  // 64 bytes detached Ed25519 signature
  bool   force             = 5;  // skip version comparison
}

message Shutdown {
  string reason   = 1;
  int32  delay_ms = 2;
}

message Ack {
  string in_response_to = 1;  // "handshake" | "heartbeat"
  string controller_version = 2;  // AgentGateway version at the protocol edge
}
```

### 3.1  Message flow

```
Agent                                   AgentGateway / Control Plane Edge
  │                                          │
  │──── Handshake ─────────────────────────►│  (first message after mTLS)
  │                                          │
  │◄──── Ack (handshake) ──────────────────│  (gateway confirms stream acceptance)
  │                                          │
  │──── Heartbeat (every 30 s) ────────────►│
  │◄──── Ack (heartbeat) ──────────────────│
  │                                          │
  │◄──── CommandRequest (request_id=A) ────│  (broker-originated task relayed by gateway)
  │                                          │
  │      [ agent executes locally ]          │
  │                                          │
  │──── CommandResponse (request_id=A) ────►│  (gateway relays result back to broker)
  │                                          │
  │◄──── UpdateDirective ──────────────────│  (broker-originated update relayed by gateway)
  │      [ agent downloads, verifies,        │
  │        stages, restarts ]                │
  │                                          │
  │──── Handshake (new version) ───────────►│  (agent reconnects post-update)
```

### 3.2  Command types and typed parameters

| `command_type` | `parameters_json` schema | `result_json` schema |
|----------------|--------------------------|----------------------|
| `Shell` | _(empty — uses `command_text`)_ | _(empty — uses `stdout`/`stderr`)_ |
| `PatchScan` | `{ "filter": { "severity": "Critical" } }` | `PatchInfo[]` |
| `PatchApply` | `{ "patchIds": [...], "allowReboot": false, "dryRun": true }` | `PatchResult` |
| `ServiceControl` | `{ "serviceName": "nginx", "action": "Restart" }` | `ServiceActionResult` |
| `SystemInfo` | `{ "sections": ["hardware","os","disks"] }` | `AgentMetadata` |

All `result_json` payloads match the existing Abstractions DTOs serialized as JSON.

---

## 4  Security Model

### 4.1  Authentication: mTLS with private CA

```
Private CA (on control machine)
├── ca.pfx  — ECDSA P-384, never leaves the control machine
├── ca.crt  — public root, distributed to every agent
│
├── server.pfx  — CN=homemanagement-control, signed by CA
│                 SAN: DNS:control-hostname, IP:control-ip
│
└── agent-{hostname}.pfx  — CN=hm-agent-{hostname}, signed by CA
                            SAN: DNS:{hostname}
                            EKU: id-kp-clientAuth
```

**Handshake sequence:**

1. Agent opens TCP to `controlServer:9444`.
2. TLS handshake — both sides present certificates.
3. **Controller validates agent cert:**
   - Issuer = private CA
   - Not expired, not on CRL
   - CN matches the `agent_id` in the subsequent `Handshake` protobuf
4. **Agent validates server cert:**
  - Issuer = `serverCaCertPath` when configured, otherwise bundled `ca.crt`
   - CN = expected control server name
5. gRPC stream established; agent sends `Handshake` message.
6. Controller sends `Ack`; agent is now in the connected pool.

**Certificate lifecycle:**

| Event | Action |
|-------|--------|
| Provisioning | Admin generates agent cert via GUI → exports `.pfx` + `ca.crt` → copies to agent machine |
| Renewal | Before expiry (default 365 d), controller emits `AuditAction.SettingsChanged`; admin regenerates |
| Revocation | Admin adds serial to CRL; controller rejects agent on next connect; existing stream torn down |
| CA rotation | New CA issued; agents receive new `ca.crt` via update directive; old CA rejected after grace period |

### 4.2  Authorization: command-level controls

The agent enforces a **local allowlist** and the controller enforces **server-side policy**.

#### Agent-side (defense in depth)

| Rule | Enforcement |
|------|-------------|
| **Command allowlist** | `CommandValidator` permits only known `command_type` values; rejects unrecognized types. Shell commands are further checked against an optional regex blocklist in config (`deniedCommandPatterns`). |
| **Rate limiting** | Sliding-window rate limiter: max 10 commands/sec, max 5 concurrent. Excess commands receive `ErrorCategory.Transient` with "rate limited". |
| **Elevation guard** | `ElevationMode != None` requires `AllowElevation = true` in agent config. Disabled by default. |
| **No outbound data exfil** | Agent never initiates file uploads to controller unless in response to a `CommandRequest`. |
| **Audit trail** | Every command (including denied ones) is logged locally with `request_id` and `correlation_id`. |

#### Controller-side

| Rule | Enforcement |
|------|-------------|
| **Agent identity** | Controller verifies CN from TLS matches `agent_id` from `Handshake`. Mismatch → disconnect + `AuditAction.AgentDisconnected`. |
| **Version gate** | Controller can enforce minimum agent version; older agents receive `UpdateDirective` before commands. |
| **Concurrency cap** | `IAgentGateway.SendCommandAsync` uses semaphore per agent (default 5 concurrent). |

### 4.3  Data protection

| Data | At rest | In transit |
|------|---------|------------|
| Agent config (`hm-agent.json`) | File ACLs: owner = service account, mode 600 | N/A |
| Agent certificate (`agent.pfx`) | File ACLs: owner = service account, mode 600 | N/A |
| Command payloads | Not persisted | TLS 1.3 (AES-256-GCM) |
| Command results | Local log file (7 d retention, auto-rotated) | TLS 1.3 |
| Heartbeat telemetry | Not persisted on agent | TLS 1.3 |

### 4.4  Threat mitigations (from STRIDE in doc 09)

| Threat | Mitigation |
|--------|-----------|
| Rogue agent | mTLS — cert must be CA-signed; CN must match `agent_id` |
| Impersonated controller | Agent validates server cert against pinned CA |
| Tampered binary | SHA-256 + Ed25519 signature verified before update install |
| Command replay | `request_id` is UUID; agent tracks recent IDs and rejects duplicates (1-hour window) |
| DoS via command flood | Agent-side rate limiter (10/s burst, 5 concurrent) |
| Credential leak | No credentials stored on agent; mTLS uses cert-based auth only |
| Privilege escalation | Elevation disabled by default; explicit flag + config gate |

---

## 5  Command Execution Engine

### 5.1  Shell commands

```
CommandRequest (command_type = "Shell")
  │
  ▼
CommandDispatcher
  │  resolve handler
  ▼
ShellCommandHandler
  │
  ├──[1] Validate: check blocklist, rate limit
  │
  ├──[2] Build ProcessStartInfo
  │       ├── Linux:  /bin/bash -c "{command_text}"
  │       │           + sudo prefix if ElevationMode = Sudo
  │       │           + sudo -u {user} if ElevationMode = SudoAsUser
  │       └── Windows: powershell.exe -NoProfile -NonInteractive -Command "{command_text}"
  │                    + RunAs verb if ElevationMode = RunAsAdmin
  │
  ├──[3] Apply environment overrides from request
  │
  ├──[4] Start process with redirected stdout/stderr
  │
  ├──[5] Await with CancellationToken (timeout_seconds)
  │       ├── Normal exit → capture exit code
  │       └── Timeout → Kill process tree → timed_out = true
  │
  └──[6] Return CommandResponse
          ├── exit_code, stdout (truncated at 1 MB), stderr
          ├── duration_ms
          └── error_category (if non-zero exit)
```

**Process isolation:**
- Each command spawns a new process — no persistent shell sessions.
- Working directory: agent install directory.
- Environment: inherits service account env + request overrides.
- Process group: on Linux, `setsid` so the entire tree can be killed on timeout.
- On Windows, `Job Object` ensures child processes are terminated.

**Inbound command queue (AgentHostService):**
- The gRPC receive loop writes incoming `CommandRequest` messages into a bounded `Channel<CommandRequest>` (capacity 128, `BoundedChannelFullMode.Wait`).
- A dedicated `CommandProcessingLoopAsync` task reads from the channel and dispatches to `HandleCommandAsync` with `SemaphoreSlim`-bounded concurrency.
- This decouples the gRPC stream read from command execution, preventing a slow command from blocking reception of subsequent requests.
- On shutdown, the channel writer is completed and the processing loop drains remaining items before exiting.

### 5.2  Typed command handlers

Typed handlers parse `parameters_json`, delegate to OS-specific strategies, and
return structured `result_json`. They do **not** use raw shell invocation for
core operations — instead they call OS APIs or well-known CLI tools with
validated arguments.

**PatchCommandHandler:**

```
PatchScan flow:
  Linux  → apt list --upgradable | parse → PatchInfo[]
           yum check-update | parse → PatchInfo[]
  Windows→ Windows Update Agent COM API → PatchInfo[]

PatchApply flow:
  Linux  → apt-get install {validated-ids} -y | parse → PatchResult
           yum update {validated-ids} -y | parse → PatchResult
  Windows→ Windows Update Agent COM API → PatchResult
```

**ServiceCommandHandler:**

```
Linux:
  Status  → systemctl show {service} --property=... → ServiceInfo
  Start   → systemctl start {service}
  Stop    → systemctl stop {service}
  Restart → systemctl restart {service}
  Enable  → systemctl enable {service}
  Disable → systemctl disable {service}

Windows:
  Status  → Get-Service {service} | ConvertTo-Json → ServiceInfo
  Start   → Start-Service {service}
  Stop    → Stop-Service {service}
  Restart → Restart-Service {service}
  Enable  → Set-Service {service} -StartupType Automatic
  Disable → Set-Service {service} -StartupType Disabled
```

All service/patch names are validated against strict regex patterns
(alphanumeric, hyphens, dots) to prevent command injection.

**SystemInfoHandler:**

```
Linux:
  CPU    → /proc/cpuinfo
  RAM    → /proc/meminfo
  Disks  → df -B1 --output=target,size,avail
  OS     → /etc/os-release + uname -r

Windows:
  CPU    → WMI Win32_Processor
  RAM    → WMI Win32_ComputerSystem
  Disks  → WMI Win32_LogicalDisk
  OS     → WMI Win32_OperatingSystem + [Environment]::OSVersion
```

---

## 6  Update Mechanism

### 6.1  Update flow

```
Controller                                Agent
    │                                        │
    │  1. Admin clicks "Update Agent"        │
    │  2. Controller builds UpdateDirective  │
    │     ├── target_version                 │
    │     ├── download_url (HTTPS)           │
    │     ├── binary_sha256                  │
    │     └── signature_ed25519              │
    │                                        │
    │──── UpdateDirective ─────────────────►│
    │                                        │
    │                3. Agent receives:       │
    │                   ├── Compare versions  │
    │                   │   (skip if same     │
    │                   │    unless force)     │
    │                   │                     │
    │                4. Download binary via    │
    │                   HTTPS from download_url│
    │                   to temp staging dir    │
    │                                        │
    │                5. Verify integrity:      │
    │                   ├── SHA-256 hash match │
    │                   └── Ed25519 signature  │
    │                                        │
    │                6. If verification fails: │
    │                   ├── Log error          │
    │                   ├── Delete temp file   │
    │                   └── Send CommandResponse│
    │                        with error        │
    │                                        │
    │                7. Stage update:          │
    │                   ├── Copy new binary    │
    │                   │   to staging/        │
    │                   ├── Set permissions    │
    │                   └── Write update marker│
    │                                        │
    │                8. Graceful restart:      │
    │     ┌────────    ├── Drain pending cmds  │
    │     │            ├── Close gRPC stream   │
    │     │            ├── Swap binary:        │
    │     │            │   mv staging/hm-agent │
    │     │            │      → hm-agent       │
    │     │            └── Exit(0)            │
    │     │                                   │
    │     │  9. Service manager restarts       │
    │     │     the process (systemd/SCM)      │
    │     │                                   │
    │     │  10. New binary starts             │
    │     └──── Handshake (new version) ──────│
    │                                        │
    │  11. Controller verifies new version    │
    │      matches target_version             │
    │  12. Emits AuditAction.AgentUpdated     │
```

### 6.2  Rollback

If the new binary fails to start (crashes within 60 s of launch):

1. **Systemd:** `Restart=on-failure` with `StartLimitBurst=3`.
   After 3 consecutive failures, the unit enters `failed` state.
   A companion `hm-agent-watchdog.service` detects this and rolls back:
   ```bash
   # /opt/hm-agent/rollback.sh
   cp /opt/hm-agent/backup/hm-agent /opt/hm-agent/hm-agent
   systemctl restart hm-agent
   ```

2. **Windows Service:** Recovery options set via `sc.exe failure`:
   - First failure: restart after 5 s
   - Second failure: restart after 30 s
   - Third failure: run rollback script
   ```
   powershell -File C:\ProgramData\HMAgent\rollback.ps1
   ```

3. **Backup retention:** The previous binary is always kept in `backup/` until the
   next successful update cycle.

### 6.3  Update security gates

| Gate | Check |
|------|-------|
| 1. Version comparison | `target_version > current_version` (unless `force = true`) |
| 2. HTTPS download | TLS-validated download from controller's update endpoint |
| 3. SHA-256 hash | `SHA256(downloaded_file) == UpdateDirective.binary_sha256` |
| 4. Ed25519 signature | Verify detached signature against embedded public key |
| 5. File permissions | Staged binary gets owner = service account, mode 750 |
| 6. Post-restart health | Controller checks new `Handshake.agent_version` matches target |

---

## 7  Failure and Retry Logic

### 7.1  Connection resilience

The agent must tolerate controller restarts, network blips, and prolonged outages.

```
Connection State Machine:

  ┌─────────┐      TLS handshake OK       ┌───────────┐
  │         ├─────────────────────────────►│           │
  │  Disconn │                              │ Connected │
  │  ected  │◄─────────────────────────────┤           │
  │         │   stream error / EOF /        │           │
  └────┬────┘   heartbeat timeout           └─────┬─────┘
       │                                          │
       │  sleep(backoff)                          │  every 30 s
       │                                          │
       ▼                                          ▼
  ┌─────────┐                              ┌───────────┐
  │ Reconnect│                              │ Heartbeat │
  │ ing     ├──── attempt connect ────►     │ Sent      │
  └─────────┘                               └───────────┘
```

**Exponential backoff with jitter:**

```
delay = min(baseDelay × 2^attempt, maxDelay) × (1 + random(-0.2, +0.2))

Default values:
  baseDelay = 1 second
  maxDelay  = 5 minutes
  jitter    = ±20%

Sequence: 1s → 2s → 4s → 8s → 16s → 32s → 64s → 128s → 256s → 300s (cap)
```

Reset behavior:
- Successful `Ack` from controller → reset `attempt` to 0.
- Each failed attempt → increment `attempt`.
- Agent process restart → reset `attempt` to 0.

### 7.2  Heartbeat and dead-connection detection

| Parameter | Default | Configurable |
|-----------|---------|-------------|
| Heartbeat interval | 30 s | `heartbeatIntervalSeconds` in config |
| Controller timeout | 90 s (3× heartbeat) | Server-side config |
| Agent-side ping | gRPC keepalive at 20 s | Hardcoded |
| gRPC keepalive timeout | 10 s | Hardcoded |

**Detection flow:**

```
Agent side:
  If no Ack received for 2× heartbeat interval → assume stream is dead → reconnect.
  gRPC transport keepalive provides a secondary signal.

Controller side:
  If no Heartbeat received for 3× heartbeat interval:
    1. Emit AgentConnectionEvent(HeartbeatTimeout)
    2. Mark agent as disconnected in connected-agents list
    3. Log AuditAction.AgentDisconnected with reason "heartbeat_timeout"
```

### 7.3  Command execution failures

| Failure mode | Agent behavior | Controller behavior |
|-------------|----------------|---------------------|
| **Process timeout** | Kill process tree; return `CommandResponse` with `timed_out = true`, `exit_code = -1` | Mark machine result as failed; may retry per `RetryPolicy` |
| **Process crash** | Catch exception; return `CommandResponse` with `error_category = "SystemError"` | Log, do not retry (non-transient) |
| **Command rejected (rate limit)** | Return `CommandResponse` with `error_category = "Transient"`, `stderr = "Rate limited"` | Wait and retry after backoff |
| **Command rejected (blocklist)** | Return with `error_category = "Authorization"` | Do not retry; alert admin |
| **Stream lost mid-command** | Command process continues; result is logged locally but lost | Controller times out `SendCommandAsync`; retry if policy allows |
| **Agent crash mid-command** | OS terminates child processes; result lost | Controller times out; retry if policy allows |

### 7.4  Retry policy (controller-side, per job)

Jobs define a `RetryPolicy` that the orchestration layer evaluates:

```
RetryPolicy:
  MaxRetries  : int   (default 3)
  BaseDelay   : TimeSpan (default 5 s)
  MaxDelay    : TimeSpan (default 2 min)

Retryable categories:
  - Transient        → yes (rate limit, network blip)
  - TargetError      → yes (command returned non-zero, may be transient)
  - Authentication   → no  (cert issue — fix required)
  - Authorization    → no  (command blocked — policy issue)
  - ConfigurationError → no
  - SystemError      → no
```

### 7.5  Local log buffer

When disconnected, the agent continues to log commands and results to the local
log file. On reconnect, it does **not** replay buffered results — the controller
is responsible for re-issuing any commands that timed out. This avoids stale-data
conflicts and simplifies the protocol.

---

## 8  Configuration

### 8.1  Agent configuration file

```jsonc
// /opt/hm-agent/hm-agent.json  (Linux)
// C:\ProgramData\HMAgent\hm-agent.json  (Windows)
{
  // ── Connection ──
  "controlServer": "mgmt.home.lan:9444",
  "agentId": "server-01",

  // ── Certificates ──
  "certPath": "certs/agent.pfx",
  "caCertPath": "certs/ca.crt",
  "serverCaCertPath": "certs/server-ca.crt",

  // ── Behavior ──
  "heartbeatIntervalSeconds": 30,
  "maxConcurrentCommands": 5,
  "commandRateLimit": 10,          // per second
  "allowElevation": false,

  // ── Security ──
  "deniedCommandPatterns": [
    "rm\\s+-rf\\s+/",
    "format\\s+[a-z]:",
    ":(){ :|:& };:"
  ],

  // ── Logging ──
  "logLevel": "Information",
  "logRetentionDays": 7,

  // ── Update ──
  "autoUpdateEnabled": true,
  "updateStagingDir": "staging/"
}
```

### 8.2  Directory layout

```
Linux: /opt/hm-agent/
Windows: C:\ProgramData\HMAgent\
macOS: /Library/Application Support/HomeManagement/Agent/

├── hm-agent(.exe)          # Main binary
├── hm-agent.json           # Configuration (ACL: 600 / service account only)
├── certs/
│   ├── agent.pfx           # Agent identity cert (ACL: 600)
│   ├── ca.crt              # Client-cert CA trust anchor (ACL: 644)
│   └── server-ca.crt       # Optional server TLS trust anchor (ACL: 644)
├── logs/
│   ├── agent-20260314.log  # Rotated daily, 7-day retention
│   └── ...
├── staging/                # Temp dir for update downloads
├── backup/
│   └── hm-agent(.exe)      # Previous binary (rollback target)
└── scripts/                # Optional custom scripts referenced by commands
```

### 8.3  Service installation

**Linux (systemd):**

```ini
# /etc/systemd/system/hm-agent.service
[Unit]
Description=HomeManagement Agent
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=/opt/hm-agent/hm-agent
WorkingDirectory=/opt/hm-agent
User=hm-agent
Group=hm-agent
Restart=on-failure
RestartSec=5
StartLimitBurst=3
StartLimitIntervalSec=120

# Security hardening
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/opt/hm-agent/logs /opt/hm-agent/staging /opt/hm-agent/backup
PrivateTmp=true
ProtectKernelTunables=true
ProtectControlGroups=true

# Capability for managing other services (when elevation allowed)
AmbientCapabilities=CAP_NET_BIND_SERVICE

[Install]
WantedBy=multi-user.target
```

**macOS (launchd):**

```xml
<!-- /Library/LaunchDaemons/net.cowgomu.hm-agent.plist -->
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
  <dict>
    <key>Label</key>
    <string>net.cowgomu.hm-agent</string>
    <key>ProgramArguments</key>
    <array>
      <string>/Library/Application Support/HomeManagement/Agent/hm-agent</string>
    </array>
    <key>WorkingDirectory</key>
    <string>/Library/Application Support/HomeManagement/Agent</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/Library/Application Support/HomeManagement/Agent/logs/launchd.stdout.log</string>
    <key>StandardErrorPath</key>
    <string>/Library/Application Support/HomeManagement/Agent/logs/launchd.stderr.log</string>
  </dict>
</plist>
```

**Windows Service:**

```powershell
# Installation via sc.exe
sc.exe create HMAgent `
    binPath= "C:\ProgramData\HMAgent\hm-agent.exe" `
    start= auto `
    obj= "NT SERVICE\HMAgent" `
    DisplayName= "HomeManagement Agent"

# Recovery options
sc.exe failure HMAgent reset= 86400 actions= restart/5000/restart/30000/run/60000 `
    command= "powershell.exe -File C:\ProgramData\HMAgent\rollback.ps1"
```

---

## 9  Platform Abstraction

The agent uses a strategy pattern to abstract OS-specific operations:

```
ICommandHandler
├── ShellCommandHandler
│   ├── LinuxShellStrategy     → /bin/bash -c
│   ├── WindowsShellStrategy   → powershell.exe -NoProfile -NonInteractive -Command
│   └── MacOsShellStrategy     → /bin/zsh -c (fallback /bin/bash -c)
│
├── PatchCommandHandler
│   ├── AptPatchStrategy       → apt / apt-get
│   ├── YumDnfPatchStrategy    → yum / dnf
│   ├── WindowsUpdateStrategy  → Windows Update Agent API
│   └── MacOsPatchStrategy     → softwareupdate / brew
│
├── ServiceCommandHandler
│   ├── SystemdServiceStrategy → systemctl
│   ├── WindowsServiceStrategy → sc.exe / Get-Service
│   └── LaunchdServiceStrategy → launchctl
│
└── SystemInfoHandler
    ├── LinuxSystemInfoStrategy  → /proc, uname, os-release
  ├── WindowsSystemInfoStrategy→ WMI, Environment, Registry
  └── MacOsSystemInfoStrategy  → sysctl, sw_vers, diskutil
```

Strategy selection happens once at startup via `AgentPlatformDetector`:

```csharp
// Simplified selection logic
var platform = AgentPlatformDetector.Detect();
// platform.OsType     → Windows | Linux | MacOs
// platform.InitSystem → Systemd | WindowsService | Launchd
// platform.PkgManager → Apt | Yum | Dnf | WindowsUpdate | SoftwareUpdate | Homebrew
```

### 9.1  Platform implementation map

| Platform | Service model | Config path | Command shell | Service control | Patch/update substrate |
|----------|---------------|-------------|---------------|-----------------|------------------------|
| Linux | `systemd` | `/opt/hm-agent/hm-agent.json` | `/bin/bash -c` | `systemctl` | `apt`, `yum`, `dnf` |
| Windows | SCM service | `C:\ProgramData\HMAgent\hm-agent.json` | `powershell.exe -NoProfile -NonInteractive -Command` | `Get-Service`, `sc.exe` | Windows Update Agent API |
| macOS | `launchd` LaunchDaemon | `/Library/Application Support/HomeManagement/Agent/hm-agent.json` | `/bin/zsh -c` with `/bin/bash -c` fallback | `launchctl` | `softwareupdate`, optional Homebrew |

Implementation notes:

- Linux remains the production baseline: self-contained `linux-x64` and `linux-arm64` binaries, dedicated `hm-agent` account, hardened `systemd` unit.
- Windows keeps native service integration: SCM registration, PowerShell shell strategy, Job Object cleanup, and Windows Update API for patch orchestration.
- macOS should be implemented as a first-class transport peer, not a compatibility shim: publish `osx-x64` and `osx-arm64`, install a LaunchDaemon, add `LaunchdServiceStrategy`, `MacOsSystemInfoStrategy`, and `MacOsPatchStrategy`, and keep file-based cert/config handling initially aligned with Linux and Windows.

---

## 10  Observability

### 10.1  Structured logging

All log entries are JSON, emitted via Serilog with these standard properties:

```json
{
  "Timestamp": "2026-03-14T10:30:00.123Z",
  "Level": "Information",
  "MessageTemplate": "Command {CommandType} completed",
  "Properties": {
    "AgentId": "server-01",
    "RequestId": "a1b2c3d4-...",
    "CorrelationId": "job-5678-...",
    "CommandType": "PatchScan",
    "ExitCode": 0,
    "DurationMs": 1234,
    "MachineName": "server-01"
  }
}
```

### 10.2  Sensitive data redaction

The agent's Serilog pipeline includes a destructuring policy that redacts:
- Certificate file paths (replaced with `***`)
- Environment variable values matching `*PASSWORD*`, `*SECRET*`, `*KEY*`
- Any `stdout`/`stderr` content matching configurable patterns

### 10.3  Audit correlation

Every command carries a `correlation_id` that flows:

```
GUI (user clicks) → Job (correlation_id) → CommandRequest (correlation_id)
  → Agent log entry → CommandResponse → AuditEvent (CorrelationId)
```

This enables end-to-end tracing from user action to agent execution.

### 10.4  Health metrics (via heartbeat)

The `Heartbeat` message doubles as a lightweight health telemetry payload:

| Metric | Source (Linux) | Source (Windows) |
|--------|---------------|-----------------|
| `uptime_seconds` | `clock_gettime(CLOCK_MONOTONIC)` | `Environment.TickCount64` |
| `cpu_percent` | `/proc/stat` delta over interval | `PerformanceCounter` |
| `memory_used_bytes` | `/proc/meminfo` MemTotal - MemAvailable | `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` |
| `disk_free_bytes` | `statvfs("/")` | `DriveInfo.AvailableFreeSpace` |

---

## 11  Concurrency Model

```
AgentHostService (main thread)
│
├── gRPC receive loop  ─── reads ControlMessage from stream
│   │
│   ├── CommandRequest → CommandDispatcher
│   │   │
│   │   └── SemaphoreSlim(maxConcurrentCommands)
│   │       │
│   │       ├── Task 1: ShellCommandHandler.HandleAsync(...)
│   │       ├── Task 2: PatchCommandHandler.HandleAsync(...)
│   │       └── ... up to maxConcurrentCommands
│   │
│   ├── UpdateDirective → UpdateCommandHandler (exclusive — blocks new commands)
│   │
│   └── Shutdown → graceful drain + exit
│
├── gRPC send loop  ─── writes AgentMessage to stream (thread-safe queue)
│   ├── Heartbeat (periodic timer)
│   └── CommandResponse (queued by handlers)
│
└── Reconnect watchdog  ─── monitors stream health, triggers reconnect
```

**Key invariants:**
- At most `maxConcurrentCommands` executing simultaneously.
- `UpdateDirective` processing acquires all semaphore slots → drains in-flight commands first.
- gRPC stream writes are serialized through a `Channel<AgentMessage>` (thread-safe producer/consumer).
- Heartbeat timer fires independently; if the send queue is backed up, heartbeat is dropped (not queued).

---

## 12  Integration with Control Machine

### 12.1  Transport layer integration

The control machine's `IRemoteExecutor` routes `TransportProtocol.Agent` to `IAgentGateway`:

```
IRemoteExecutor.ExecuteAsync(target, command)
  │
  ├── target.Protocol == SSH      → SshTransportProvider
  ├── target.Protocol == WinRM    → WinRmTransportProvider
  ├── target.Protocol == PSRemoting → PSRemotingTransportProvider
  └── target.Protocol == Agent    → AgentTransportProvider
                                      │
                                      └── IAgentGateway.SendCommandAsync(agentId, command)
                                            │
                                            └── Serialize to CommandRequest protobuf
                                                → write to agent's gRPC stream
                                                → await CommandResponse
                                                → deserialize to RemoteResult
```

### 12.2  Metadata refresh

When the inventory subsystem runs `MetadataRefresh` for an agent-mode machine:

1. Controller sends `CommandRequest(command_type = "SystemInfo")`.
2. Agent's `SystemInfoHandler` collects hardware/OS data.
3. Response `result_json` is deserialized to `AgentMetadata`.
4. Controller updates `MachineEntity` fields (CPU, RAM, disks, OS version).

### 12.3  Audit integration

Agent-related audit events:

| Event | Trigger |
|-------|---------|
| `AgentConnected` | Agent sends `Handshake`, controller validates |
| `AgentDisconnected` | Stream error, heartbeat timeout, or `Shutdown` |
| `AgentUpdated` | Post-update `Handshake` confirms new version |
| All command events | Normal `PatchInstallStarted`, `ServiceStopped`, etc. — agent is just the transport |

---

## 13  Deployment and Provisioning

### 13.1  First-time setup

```
1. Admin adds machine in GUI with ConnectionMode = Agent
2. GUI generates agent certificate:
   a. Private key → agent-{hostname}.pfx
   b. Sign with CA
   c. Bundle ca.crt
3. Admin downloads provisioning package (zip):
   ├── hm-agent(.exe)        — platform-specific binary
   ├── hm-agent.json          — pre-configured with controlServer, agentId
   ├── certs/
   │   ├── agent.pfx
  │   ├── ca.crt
  │   └── server-ca.crt      — optional, required when ingress TLS uses a different issuer than agent mTLS
   ├── install-linux.sh       — copies files, creates user, installs systemd unit
  ├── install-windows.ps1    — copies files, creates service, sets ACLs
  └── install-macos.sh       — copies files, installs LaunchDaemon, sets root-only permissions
4. Admin copies package to target, runs install script
5. Agent starts → connects to controller → appears in GUI
```

### 13.2  Build matrix

| RID | OS | Architecture | Self-contained |
|-----|-----|-------------|----------------|
| `win-x64` | Windows 10+ / Server 2016+ | x86_64 | Yes (.exe) |
| `linux-x64` | Ubuntu 20.04+, RHEL 8+, Debian 11+ | x86_64 | Yes (ELF) |
| `linux-arm64` | Ubuntu 20.04+, Debian 11+, Raspberry Pi OS 64-bit | ARM64 | Yes (ELF) |
| `osx-x64` | macOS 13+ | x86_64 | Yes (Mach-O) |
| `osx-arm64` | macOS 13+ | ARM64 / Apple Silicon | Yes (Mach-O) |

```bash
# Build command
dotnet publish src/HomeManagement.Agent -c Release \
  -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true \
  -p:EnableCompressionInSingleFile=true
```

Expected binary sizes: ~10–15 MB (trimmed, compressed).

---

## 14  Testing Strategy

| Layer | What | How |
|-------|------|-----|
| **Unit** | CommandDispatcher routing, CommandValidator allowlist/rate-limit, ReconnectPolicy backoff math, IntegrityChecker SHA-256/Ed25519 | xUnit, in-process, no I/O |
| **Integration** | Full gRPC round-trip (in-process server + client), certificate validation, handler → OS command execution | xUnit + TestServer, ephemeral certs |
| **Platform** | Service install/uninstall, systemd notify, Windows SCM integration, update swap + rollback | VM-based (Linux + Windows), scripted |
| **Security** | Expired cert rejection, CRL check, CN mismatch, rate limiter under load, blocklist enforcement | xUnit + crafted certs |

---

## 15  Future Considerations

| Item | Description | Priority |
|------|-------------|----------|
| **File transfer** | `CommandRequest` with type `FileTransfer` for pushing/pulling files through the gRPC stream. Chunked streaming with SHA-256 per chunk. | P1 |
| **Plugin system** | Custom `ICommandHandler` implementations loaded from `plugins/` directory. Signed DLLs only. | P2 |
| **Agent groups** | Tag-based grouping (mirrors machine tags) for broadcasting commands to agent subsets. | P2 |
| **Offline command queue** | Controller queues commands while agent is disconnected; delivers on reconnect. Requires idempotency tokens. | P3 |
| **Prometheus endpoint** | Optional `/metrics` HTTP endpoint on agent for external monitoring integration. | P3 |
