# Security Model, Deployment, Error Handling & Scalability

---

## 1. Security Model

### 1.1 Threat Model

| Threat | Attack Vector | Mitigation |
|---|---|---|
| **Credential theft at rest** | Attacker reads vault file from disk | AES-256-GCM encryption, master password via Argon2id KDF (64 MB memory, 3 iterations, 4 parallelism) |
| **Credential theft in memory** | Memory dump / cold boot | Pinned `SecureBuffer`, zeroed on dispose, vault auto-lock on idle |
| **Man-in-the-middle (SSH)** | Intercepted SSH session | Host key fingerprint verification, known_hosts enforcement |
| **Man-in-the-middle (WinRM)** | Intercepted WinRM session | HTTPS transport (port 5986), server certificate validation |
| **Man-in-the-middle (Agent)** | Intercepted gRPC stream | Mutual TLS (mTLS) — both agent and control authenticate via certs |
| **Unauthorized agent** | Rogue agent connects to control | Agent certificates signed by private CA; only known certs accepted |
| **Privilege escalation on target** | Command runs with excessive privilege | Least-privilege: use `sudo` / `RunAs` only when needed, scoped to specific commands |
| **Injection via command construction** | Malicious input in hostnames/service names | Parameterized command builders, input validation, allowlist patterns |
| **Audit log tampering** | Attacker modifies audit trail | Append-only audit table, HMAC chain on events (each event HMAC includes previous hash) |
| **Lateral movement** | Compromised control machine used to attack all targets | Per-machine credentials, no shared admin accounts, session time limits |
| **Supply chain attack** | Compromised NuGet package | Lock file (`packages.lock.json`), verified package signatures, dependency audit |
| **Agent binary tampering** | Modified agent binary deployed | Code-signed agent binaries, SHA-256 integrity verification before execution |

### 1.2 Authentication Model

```
┌─────────────────────────────────────────────────────────┐
│                    Authentication Layers                  │
│                                                          │
│  1. Application Start                                    │
│     └── Master password → unlocks vault                  │
│         └── Argon2id(password, salt) → vault key         │
│                                                          │
│  2. Machine Connection (Agentless)                       │
│     └── Credential retrieved from vault                  │
│         ├── SSH: key-based auth (preferred) or password  │
│         ├── WinRM: Kerberos (preferred) or NTLM/Basic   │
│         └── PS Remoting: same as WinRM                   │
│                                                          │
│  3. Agent Connection                                     │
│     └── mTLS handshake                                   │
│         ├── Agent presents client certificate            │
│         ├── Control presents server certificate          │
│         └── Both signed by same private CA               │
│                                                          │
│  4. Audit Identity                                       │
│     └── OS user running control app = audit actor        │
│         └── Captured from Environment.UserName            │
└─────────────────────────────────────────────────────────┘
```

### 1.3 Authorization Model

The corrected platform baseline no longer assumes a single trusted operator for the platform runtime. `hm-auth` is the system-of-record auth boundary, and RBAC is enforced through issued identities and protected admin APIs.

```
Role → Permission mapping:
  Admin    → Full access (all machines, vault, settings)
  Operator → Execute patches, control services (scoped to machine groups)
  Viewer   → Read-only (inventory, audit logs, status)
```

### 1.3.1 Correction-Release Auth Baseline

- Local username/password authentication is implemented using Argon2id password hashes
- Access and refresh tokens are issued by `hm-auth`
- Refresh token revocation is persisted
- Admin APIs require authenticated `Admin` role membership
- Bootstrap admin seeding is supported for first-run environments

### 1.4 Secure Defaults

| Setting | Default |
|---|---|
| Vault auto-lock timeout | 15 minutes |
| SSH host key verification | Strict (fail on unknown) |
| WinRM transport | HTTPS only |
| Agent TLS | mTLS required, TLS 1.3 minimum |
| Credential display in GUI | Masked, never shown in full |
| Log credential values | Never — redacted by Serilog destructuring policy |
| Session timeout (to target) | 5 minutes idle |
| Max concurrent connections | 10 per machine, 50 total |

---

## 2. Deployment Model

### 2.1 Control Application Deployment

Supported production artifact for the platform runtime:

- use the Helm chart in `deploy/helm/homemanagement`
- keep environment-specific secrets, TLS, and ingress policy in Helm values overrides
- treat `deploy/kubernetes/` as reference-only scaffolding unless it is explicitly brought back to parity

```
Distribution: Single self-contained executable
Platform:     win-x64, linux-x64 (publish profiles)
Size:         ~80-120 MB (framework-dependent: ~15 MB)
Install:      xcopy / extract zip — no installer required
Data Dir:     ~/.homemanagement/ (Linux) or %APPDATA%\HomeManagement\ (Windows)

Directory Layout:
  HomeManagement/
  ├── HomeManagement.exe          (or HomeManagement on Linux)
  ├── appsettings.json            (base configuration)
  └── appsettings.local.json      (user overrides, git-ignored)

Data Directory (~/.homemanagement/):
  ├── homemanagement.db           (SQLite database)
  ├── vault.enc                   (encrypted credential vault)
  ├── logs/
  │   ├── hm-20260314.log         (rolling daily log files)
  │   └── ...
  ├── certs/                      (CA and agent certificates)
  │   ├── ca.pfx
  │   └── ca.crt
  └── known_hosts                 (SSH host key fingerprints)
```

### 2.2 Agent Deployment

```
Distribution: Single self-contained executable (~10 MB)
Platform:     win-x64, linux-x64, linux-arm64
Install:      Copy binary + config, register as service

Agent Directory:
  /opt/hm-agent/                  (Linux)
  C:\ProgramData\HMAgent\         (Windows)
  ├── hm-agent                    (binary)
  ├── hm-agent.json               (config)
  └── certs/
      ├── agent.pfx               (agent identity cert)
      └── ca.crt                  (control machine CA cert)

hm-agent.json:
{
    "controlServer": "mgmt.home.lan:9444",
    "agentId": "server-01",
    "certPath": "certs/agent.pfx",
    "caCertPath": "certs/ca.crt",
    "heartbeatIntervalSeconds": 30,
    "logLevel": "Information"
}

Service Registration:
  Linux:   systemd unit file (provided)
  Windows: sc.exe create HMAgent binPath="C:\ProgramData\HMAgent\hm-agent.exe"
```

### 2.3 Deployment Automation (from Control App)

The control application can deploy the agent to a target machine:

```
1. GUI: User selects target → "Deploy Agent"
2. Core: Build agent package (binary + config + cert)
3. Transport: SCP/WinRM file transfer to target
4. Transport: Execute install script on target
5. Transport: Verify agent connects back to control
6. Inventory: Update machine connection mode to "Agent"
```

For CI and release validation, the minimum supported deployment check is:

- `helm lint deploy/helm/homemanagement` with non-placeholder secret values
- `helm template deploy/helm/homemanagement` with the same values to verify renderability

---

## 3. Error Handling & Retry Strategy

### 3.1 Error Classification

```csharp
public enum ErrorCategory
{
    Transient,          // Network timeout, connection reset — safe to retry
    Authentication,     // Bad credentials, expired key — do NOT retry, alert user
    Authorization,      // Permission denied — do NOT retry, alert user
    TargetError,        // Command failed on target — may retry based on context
    ConfigurationError, // Bad settings, missing dependency — needs user action
    SystemError         // Out of memory, disk full — critical alert
}
```

### 3.2 Retry Policy

```
Transient Errors (network, timeout):
  Strategy:    Exponential backoff with jitter
  Base delay:  1 second
  Max delay:   30 seconds
  Max retries: 3
  Jitter:      ±25% of delay (prevents thundering herd)
  Formula:     delay = min(baseDelay * 2^attempt + random_jitter, maxDelay)

  Attempt 1: ~1s delay
  Attempt 2: ~2s delay
  Attempt 3: ~4s delay
  → Give up, mark as failed

Authentication Errors:
  Strategy:    No retry — immediately surface to user
  Action:      Lock credential, prompt for re-entry

Target Errors (command failures):
  Strategy:    Configurable per operation
  Patches:     Retry once (some package manager locks are transient)
  Services:    Retry once with 5s delay (service may be in transition)
  Custom:      User-configurable retry count
```

### 3.3 Circuit Breaker Pattern

```
Per-machine circuit breaker:

  CLOSED (healthy)
    │
    └── N consecutive failures → OPEN
                                    │
                                    └── After cooldown period → HALF-OPEN
                                                                   │
                                          ┌── success ─── CLOSED   │
                                          └── failure ─── OPEN ────┘

Configuration:
  Failure threshold:  3 consecutive failures
  Cooldown period:    60 seconds
  Half-open probes:   1 test connection attempt
```

### 3.4 Timeout Strategy

| Operation | Default Timeout | Configurable |
|---|---|---|
| Connection establishment | 15 seconds | Yes |
| Simple command execution | 30 seconds | Yes |
| Patch detection scan | 5 minutes | Yes |
| Patch installation | 30 minutes | Yes |
| File transfer (per 100 MB) | 10 minutes | Yes |
| Service start/stop | 2 minutes | Yes |
| Agent heartbeat | 60 seconds | No |

### 3.5 Idempotency Guarantees

| Operation | Idempotent? | Strategy |
|---|---|---|
| Patch detection | Yes | Read-only scan |
| Patch installation | Yes* | Check if already installed before applying |
| Service start | Yes | No-op if already running |
| Service stop | Yes | No-op if already stopped |
| Service restart | Yes | Always restarts |
| File transfer | Yes | Checksum comparison, skip if identical |

---

## 4. Scalability Considerations

### 4.1 Current Design (Single Operator, <100 Machines)

The initial architecture is optimized for a single operator managing a home lab or small infrastructure:

- **SQLite** handles hundreds of thousands of records without issue
- **Thread pool** manages concurrent operations (configurable max parallelism)
- **Connection pooling** limits resource usage on control machine
- **In-process scheduling** via Quartz.NET with SQLite job store

### 4.2 Scaling Axes

| Axis | Limit | Next Step |
|---|---|---|
| **Machine count** | ~100 machines (SQLite + thread pool) | Swap SQLite → PostgreSQL, add worker threads |
| **Concurrent operations** | ~50 parallel connections | Connection pool tuning, consider async pipeline |
| **Data volume** | ~10 GB audit history (SQLite) | Archive old data, partitioned tables in PostgreSQL |
| **Multiple operators** | 1 (local app) | Add REST API + web dashboard, auth layer |
| **Geographic distribution** | Single LAN | VPN overlay or agent-based with hub relay |

### 4.3 Architectural Ready Points

The system is designed to evolve without rewriting:

```
v1 (Current Design):
  ┌───────────┐
  │ Local App │ ──→ SQLite ──→ SSH/WinRM/Agent
  └───────────┘

v2 (Multi-User):
  ┌───────────┐   ┌──────────┐
  │ Local App │──→│ REST API │──→ PostgreSQL ──→ SSH/WinRM/Agent
  │ Web UI    │──→│ Server   │
  └───────────┘   └──────────┘

v3 (Cloud-Ready):
  ┌───────────┐   ┌──────────┐   ┌──────────┐
  │ Web UI    │──→│ API GW   │──→│ Workers  │──→ Agent Network
  │ Mobile    │   │ (Auth)   │   │ (Queue)  │
  └───────────┘   └──────────┘   └──────────┘
                       │
                  ┌────┴────┐
                  │  Cloud  │
                  │  DB +   │
                  │  Vault  │
                  └─────────┘
```

### 4.4 Performance Targets

| Metric | Target |
|---|---|
| Machine inventory load (100 machines) | < 100 ms |
| Patch scan initiation (10 machines) | < 2 seconds |
| Service status query (single machine) | < 3 seconds |
| GUI startup (cold) | < 3 seconds |
| GUI navigation between views | < 200 ms |
| Audit log query (10,000 events) | < 500 ms |

---

## 5. Logging & Observability Design

### 5.1 Log Levels & Usage

| Level | Usage | Example |
|---|---|---|
| **Verbose** | Internal flow tracing | "Entering `PatchDetector.DetectAsync` for machine {MachineId}" |
| **Debug** | Detailed operational data | "SSH connection to {Host}:{Port} established in {Duration}ms" |
| **Information** | Key business events | "Patch scan completed for {MachineName}: {PatchCount} updates found" |
| **Warning** | Recoverable issues | "Connection to {Host} timed out, retrying (attempt {Attempt}/3)" |
| **Error** | Failed operations | "Patch installation failed on {MachineName}: {ErrorMessage}" |
| **Fatal** | Application-level failures | "Vault decryption failed — corrupt vault file?" |

### 5.2 Logging Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│                        Serilog Pipeline                            │
│                                                                    │
│  Source Code                                                       │
│    │                                                               │
│    ▼                                                               │
│  ILogger<T>.LogInformation("Patch {PatchId} applied to {Host}")   │
│    │                                                               │
│    ▼                                                               │
│  ┌──────────────────────────────────┐                              │
│  │          Enrichers               │                              │
│  │  + ThreadId                      │                              │
│  │  + MachineName (source)          │                              │
│  │  + CorrelationId                 │                              │
│  │  + Timestamp (UTC)               │                              │
│  │  + Module (via SourceContext)     │                              │
│  └──────────────┬───────────────────┘                              │
│                 │                                                   │
│                 ▼                                                   │
│  ┌──────────────────────────────────┐                              │
│  │     Destructuring Policies       │                              │
│  │  - Redact: CredentialPayload     │                              │
│  │  - Redact: SecureString          │                              │
│  │  - Redact: *.Password            │                              │
│  │  - Redact: *.PrivateKey          │                              │
│  └──────────────┬───────────────────┘                              │
│                 │                                                   │
│        ┌────────┼────────┬──────────────┐                          │
│        ▼        ▼        ▼              ▼                          │
│   ┌────────┐ ┌───────┐ ┌─────────┐ ┌────────────┐                 │
│   │Console │ │ File  │ │ SQLite  │ │  Syslog    │                 │
│   │ Sink   │ │ Sink  │ │ Sink    │ │  Sink      │                 │
│   │(Debug) │ │(Ops)  │ │(Audit)  │ │ (Future)   │                 │
│   └────────┘ └───────┘ └─────────┘ └────────────┘                 │
│                                                                    │
│   Filters:                                                         │
│   - Console: Debug+ (dev) or Information+ (prod)                   │
│   - File:    Information+ (rolling, 7-day retention, 100 MB cap)   │
│   - SQLite:  AuditEvent only (immutable, HMAC chain)               │
│   - Syslog:  Warning+ (future, for SIEM integration)              │
└────────────────────────────────────────────────────────────────────┘
```

### 5.3 Correlation ID Flow

Every user-initiated action generates a `CorrelationId` that flows through all layers:

```
GUI click → CorrelationId = Guid.NewGuid()
  → Orchestrator (logged with CorrelationId)
    → PatchService (logged with CorrelationId)
      → Transport (logged with CorrelationId)
        → AuditLogger (event tagged with CorrelationId)
```

This allows tracing a single user action through the entire log/audit trail.

### 5.4 Audit HMAC Chain

Each audit event is chained to the previous via HMAC to detect tampering:

```
Event[0].Hash = HMAC-SHA256(key, serialize(Event[0]))
Event[1].Hash = HMAC-SHA256(key, Event[0].Hash + serialize(Event[1]))
Event[N].Hash = HMAC-SHA256(key, Event[N-1].Hash + serialize(Event[N]))

Verification: walk the chain from first event, recompute HMACs.
If any hash mismatches → tamper detected.
```

### 5.5 Operational Metrics (GUI Dashboard)

| Metric | Source |
|---|---|
| Machines online / offline / unreachable | Inventory heartbeat checks |
| Patches pending / installed / failed | Patch history table |
| Active jobs / completed / failed (24h) | Job history table |
| Agent connections (current) | Agent gateway |
| Vault lock status | Vault module |
| Last patch scan time per machine | Patch scan history |

---

## 6. Future Expansion Paths

### Phase 1: Core (v1.0)
- Local GUI application
- Agentless SSH + WinRM execution
- Patch detection and application
- Service controller
- Machine inventory with metadata
- Encrypted credential vault
- Audit logging
- Basic job scheduling

### Phase 2: Agent & Automation (v1.5)
- Optional lightweight agent
- Agent auto-deployment from GUI
- Scheduled recurring patch scans
- Pre/post patch hooks (e.g., VM snapshot)
- Email/webhook notifications on failures
- CSV/JSON import/export

### Phase 3: Multi-User (v2.0)
- REST API layer (ASP.NET Core)
- Web dashboard (Blazor or React)
- PostgreSQL backend option
- Role-based access control (RBAC)
- Multi-operator support with auth
- Active Directory/LDAP integration

### Phase 4: Cloud & Scale (v3.0)
- Cloud deployment option (Azure, AWS)
- Message queue for distributed workers (RabbitMQ, Azure Service Bus)
- Container orchestration support (Docker/K8s patching)
- Ansible/Terraform integration
- Compliance reporting (CIS, STIG)
- Mobile companion app (MAUI)

### Phase 5: Intelligence (v4.0)
- Patch risk assessment (ML-based)
- Automated remediation playbooks
- Drift detection and configuration management
- Performance baseline monitoring
- Capacity planning recommendations
