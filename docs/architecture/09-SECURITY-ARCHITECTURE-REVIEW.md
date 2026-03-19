# 09 — Security Architecture Review

> **Version:** 1.0  
> **Date:** 2026-03-14  
> **Status:** Approved  
> **Classification:** Internal — Security Sensitive  
> **Supersedes:** Sections 1 and 5 of `05-SECURITY-DEPLOYMENT-SCALING.md` (retained for historical reference)

This document is a comprehensive security architecture review of the HomeManagement cross-platform patching and service-controller system. It identifies threats, attack vectors, and vulnerabilities; provides a STRIDE-based threat model; and specifies designs for credential storage, secure communication, authentication/authorization, OS hardening, audit logging, and secure update workflows.

---

## Table of Contents

1. [System Security Context](#1-system-security-context)
2. [STRIDE Threat Model](#2-stride-threat-model)
3. [Secure Credential Storage Design](#3-secure-credential-storage-design)
4. [Secure Communication Design](#4-secure-communication-design)
5. [Authentication & Authorization Model](#5-authentication--authorization-model)
6. [OS Hardening Recommendations](#6-os-hardening-recommendations)
7. [Logging & Audit Requirements](#7-logging--audit-requirements)
8. [Secure Update & Patching Workflow](#8-secure-update--patching-workflow)
9. [Vulnerability Register & Remediation Plan](#9-vulnerability-register--remediation-plan)
10. [Security Testing Requirements](#10-security-testing-requirements)

---

## 1. System Security Context

### 1.1 Trust Boundaries

```
┌─────────────────────── TRUST BOUNDARY 1: Control Machine ───────────────────┐
│                                                                              │
│  ┌──────────────────────────────────────────────────────────────────┐        │
│  │  HomeManagement GUI Process (single operator, desktop app)       │        │
│  │                                                                  │        │
│  │  ┌───────────┐  ┌──────────┐  ┌───────────┐  ┌──────────────┐  │        │
│  │  │Credential │  │ Patch    │  │ Service   │  │ Orchestration│  │        │
│  │  │  Vault    │  │ Manager  │  │Controller │  │   Engine     │  │        │
│  │  └─────┬─────┘  └────┬─────┘  └────┬──────┘  └──────┬───────┘  │        │
│  │        │              │              │                │          │        │
│  │        ▼              ▼              ▼                ▼          │        │
│  │  ┌──────────────────────────────────────────────────────────┐   │        │
│  │  │            Remote Execution Engine (Transport)            │   │        │
│  │  └──────────────┬────────────┬────────────┬─────────────────┘   │        │
│  └─────────────────┼────────────┼────────────┼─────────────────────┘        │
│                    │            │            │                                │
│  ┌─────────────────┼────────────┼────────────┼──────────────────────────┐    │
│  │  Local Storage   │            │            │                          │    │
│  │  ├── vault.enc (AES-256-GCM encrypted)                               │    │
│  │  ├── homemanagement.db (SQLite, HMAC-chained audit)                  │    │
│  │  ├── known_hosts (SSH fingerprint store)                             │    │
│  │  └── certs/ (mTLS CA + certificates)                                 │    │
│  └──────────────────────────────────────────────────────────────────────┘    │
│                                                                              │
└─── TB1 ─────────────────────────────────────────────────────────────────────┘
         │                    │                    │
         │ SSH (port 22)      │ WinRM/HTTPS        │ gRPC+mTLS
         │ TB2                │ (port 5986) TB3     │ (port 9444) TB4
         ▼                    ▼                    ▼
┌────────────────┐  ┌────────────────┐  ┌────────────────────────────────┐
│ Linux Target   │  │ Windows Target │  │ Agent-Mode Target              │
│ (Agentless)    │  │ (Agentless)    │  │ ┌────────────────────────────┐ │
│                │  │                │  │ │ hm-agent (gRPC server)     │ │
│ sshd running   │  │ WinRM enabled  │  │ │ ├── agent.pfx (identity)  │ │
│ user with sudo │  │ service acct   │  │ │ └── ca.crt (trust anchor) │ │
│                │  │                │  │ └────────────────────────────┘ │
└────────────────┘  └────────────────┘  └────────────────────────────────┘
```

### 1.2 Assets Under Protection

| Asset | Sensitivity | Location | Protection |
|---|---|---|---|
| **Credential vault file** | **CRITICAL** | `vault.enc` on disk | AES-256-GCM, Argon2id-derived key |
| **Derived encryption key** | **CRITICAL** | Process memory (transient) | GCHandle.Pinned, ZeroMemory on dispose |
| **SSH private keys** | **CRITICAL** | Inside vault (encrypted) | Two-layer encryption |
| **HMAC chain key** | **HIGH** | Stored in vault | Vault encryption protects it |
| **mTLS private keys** | **HIGH** | `certs/` directory | File system ACLs |
| **SQLite database** | **HIGH** | `homemanagement.db` | Contains machine metadata, audit trail |
| **Audit event chain** | **HIGH** | SQLite `AuditEvents` table | HMAC-SHA256 chain, append-only |
| **Known hosts fingerprints** | **MEDIUM** | `known_hosts` file | File system ACLs |
| **Application logs** | **MEDIUM** | `logs/` directory | Sensitive data redacted before write |
| **Agent binaries** | **MEDIUM** | Target machines | Code signing + SHA-256 verification |
| **Application configuration** | **LOW** | `appsettings.json` | No secrets in config files |

### 1.3 Threat Actors

| Actor | Capability | Motivation | Likelihood |
|---|---|---|---|
| **Malicious insider** | Physical/RDP access to control machine | Data theft, sabotage | Medium |
| **Network attacker** | ARP spoofing, MITM on LAN | Credential interception, lateral movement | Medium |
| **Compromised target** | Root/admin on a managed machine | Pivot to control machine, credential theft | Medium |
| **Supply-chain attacker** | Poisoned NuGet package or agent binary | Backdoor, crypto-mining | Low |
| **Remote attacker** | Exploits exposed gRPC/agent port | Unauthorized command execution | Low (LAN-only) |

---

## 2. STRIDE Threat Model

### 2.1 STRIDE Analysis by Component

#### TB1 → TB2: Control Machine → Linux Target (SSH)

| STRIDE | Threat | Risk | Mitigation | Status |
|---|---|---|---|---|
| **S** — Spoofing | Attacker impersonates target SSH server | HIGH | SSH host key verification via `known_hosts`; reject on change | ✅ Implemented |
| **T** — Tampering | MITM modifies commands/responses in transit | HIGH | SSH encrypts all traffic (ChaCha20/AES-GCM) | ✅ by protocol |
| **R** — Repudiation | Operator denies executing a patch command | MEDIUM | HMAC-chained audit trail with actor identity, correlation ID | ✅ Implemented |
| **I** — Info Disclosure | Password/key leaked to logs | HIGH | `ISensitiveDataFilter.Redact()` + Serilog destructuring | ✅ Implemented |
| **I** — Info Disclosure | SSH password observable in `ps aux` | MEDIUM | Elevation handler pipes password via stdin, not command-line arg | ✅ Designed |
| **D** — DoS | Attacker floods control machine connections | MEDIUM | Connection pool per-host limits (3 max), global limit (50) | ✅ Designed |
| **E** — Elevation | Command runs with unintended privilege | HIGH | Explicit `ElevationMode` enum; no implicit sudo | ✅ Designed |

#### TB1 → TB3: Control Machine → Windows Target (WinRM)

| STRIDE | Threat | Risk | Mitigation | Status |
|---|---|---|---|---|
| **S** — Spoofing | Attacker impersonates WinRM endpoint | HIGH | HTTPS + server certificate validation; reject self-signed unless pinned | ✅ Designed |
| **S** — Spoofing | Stolen Kerberos ticket used to authenticate | MEDIUM | Kerberos tickets have time-limited validity; enforce renewal | ⚠️ OS-managed |
| **T** — Tampering | MITM modifies PowerShell commands | HIGH | HTTPS (TLS 1.2+) encrypts transport | ✅ by protocol |
| **R** — Repudiation | Service control action not attributable | MEDIUM | Audit event with actor, machine, action, timestamp, correlation | ✅ Implemented |
| **I** — Info Disclosure | NTLM hash captured via downgrade attack | HIGH | Prefer Kerberos; disable NTLM where possible; enforce TLS | ⚠️ Config-dependent |
| **D** — DoS | WinRM service overwhelmed | LOW | Connection pool limits; circuit breaker | ✅ Designed |
| **E** — Elevation | RunAs used with over-privileged account | MEDIUM | Credential scoped per-machine; least-privilege accounts | ✅ Designed |

#### TB1 → TB4: Control Machine → Agent (gRPC + mTLS)

| STRIDE | Threat | Risk | Mitigation | Status |
|---|---|---|---|---|
| **S** — Spoofing | Rogue agent connects with forged certificate | HIGH | mTLS — agent cert must be signed by private CA; `SslServerAuthenticationOptions.ClientCertificateRequired = true` | ✅ Designed |
| **S** — Spoofing | Attacker impersonates control server | HIGH | Agent validates server cert against pinned CA | ✅ Designed |
| **T** — Tampering | Command payload modified in transit | HIGH | TLS 1.3 integrity (AEAD ciphers) | ✅ by protocol |
| **T** — Tampering | Agent binary replaced with malicious version | **CRITICAL** | Code signing + SHA-256 integrity hash in `AgentUpdatePackage` | ❌ Not enforced |
| **R** — Repudiation | Agent denies executing a command | MEDIUM | Command execution logged on both control and agent sides | ⚠️ Partial |
| **I** — Info Disclosure | Agent config file leaks control server address | LOW | Config contains only hostname, not credentials | ✅ by design |
| **D** — DoS | Flood agent with rapid command requests | MEDIUM | Agent-side command queue with rate limiting | ❌ Not implemented |
| **E** — Elevation | Agent runs with excessive privilege | HIGH | Agent runs as dedicated low-privilege service account; elevates per-command | ✅ Designed |

#### Internal: Credential Vault

| STRIDE | Threat | Risk | Mitigation | Status |
|---|---|---|---|---|
| **S** — Spoofing | Attacker bypasses vault unlock | **CRITICAL** | Argon2id KDF (64 MiB, 3 iter, 4 parallel); no bypass path | ✅ Implemented |
| **T** — Tampering | Vault file modified on disk | HIGH | AES-256-GCM authentication tag detects modification | ✅ Implemented |
| **T** — Tampering | In-memory vault entries modified | LOW | Entries are immutable records; modifications create new versions | ✅ by design |
| **R** — Repudiation | Credential accessed without trace | MEDIUM | `CredentialAccessed` audit event on every `GetPayloadAsync()` | ✅ Designed |
| **I** — Info Disclosure | Decrypted credential in memory dump | HIGH | `GCHandle.Pinned` + `CryptographicOperations.ZeroMemory()` on dispose | ✅ Implemented |
| **I** — Info Disclosure | Vault file exfiltrated and brute-forced offline | HIGH | Argon2id with 64 MiB memory makes GPU attacks expensive ($$$) | ✅ Implemented |
| **D** — DoS | Repeated failed unlock attempts consume CPU | MEDIUM | Exponential backoff + lockout after N failures | ⚠️ Designed, not enforced |
| **E** — Elevation | Low-privilege attacker reads vault file | HIGH | File ACLs restrict vault.enc to current user only | ⚠️ Not enforced by app |

#### Internal: Audit Trail

| STRIDE | Threat | Risk | Mitigation | Status |
|---|---|---|---|---|
| **S** — Spoofing | Forged audit entry with false actor identity | MEDIUM | Actor from `Environment.UserName` (OS-level); correlation context is `AsyncLocal` (not spoofable from application code) | ✅ Designed |
| **T** — Tampering | Historical audit events modified | **CRITICAL** | HMAC-SHA256 chain — modifying any event breaks chain verification | ✅ Implemented |
| **T** — Tampering | Audit database file replaced wholesale | HIGH | Chain verification against known chain key; first-event hash can be externally recorded | ⚠️ Partial |
| **R** — Repudiation | Audit system silently fails (events lost) | HIGH | `Fatal`-level alert on persistence failure; retry once for transient errors | ✅ Designed |
| **I** — Info Disclosure | Sensitive data in audit `Detail` field | MEDIUM | `ISensitiveDataFilter.Redact()` applied before persistence | ✅ Designed |
| **D** — DoS | Audit table grows unbounded | LOW | Archival strategy + monitoring; audit events are small records | ⚠️ No auto-archive |
| **E** — Elevation | Attacker disables audit logging | HIGH | Audit logger is registered via DI; no runtime disable mechanism | ✅ by design |

#### Internal: SQLite Database

| STRIDE | Threat | Risk | Mitigation | Status |
|---|---|---|---|---|
| **S** — Spoofing | Attacker replaces database file | HIGH | File ACLs; HMAC chain in audit table detects replacement | ⚠️ Partial |
| **T** — Tampering | Direct SQL modification bypassing EF Core | HIGH | File ACLs; HMAC chain for audit events; no sensitive credentials in DB | ⚠️ Partial |
| **I** — Info Disclosure | Database contains machine IPs, hostnames, OS info | MEDIUM | File ACLs; data is operational metadata, not credentials | ⚠️ File ACLs needed |
| **D** — DoS | Database lock contention | LOW | SQLite WAL mode; bounded connection pool | ✅ by design |

### 2.2 STRIDE Risk Heat Map

```
            ┌───────────────────────────────────────────────────────┐
            │                    IMPACT                              │
            │         Low         Medium         High      Critical  │
            ├──────────┬──────────┬──────────┬──────────┬───────────┤
  L    High │          │  DB DoS  │ Rogue    │ Agent    │           │
  I         │          │          │ agent    │ binary   │           │
  K         │          │          │ rate DoS │ tamper   │           │
  E  Medium │          │ Config   │ SSH host │ NTLM    │ Vault     │
  L         │          │ leak     │ key MITM │ downgrade│ brute-    │
  I         │          │ Audit    │ Priv esc │ Vault    │ force     │
  H         │          │ detail   │ via sudo │ file     │ (offline) │
  O    Low  │          │          │ Kerberos │ DB file  │           │
  O         │          │          │ ticket   │ replaced │           │
  D         │          │          │ reuse    │          │           │
            └──────────┴──────────┴──────────┴──────────┴───────────┘
```

---

## 3. Secure Credential Storage Design

### 3.1 Architecture Overview

```
                    ┌───────────────────────────────────────────┐
                    │         Credential Vault Service           │
                    │         (HomeManagement.Vault)             │
                    │                                           │
                    │  ┌──────────────────────────────────────┐ │
                    │  │        Runtime State (Memory)         │ │
                    │  │                                      │ │
                    │  │  KeyProtector (GCHandle.Pinned)      │ │
                    │  │  └── 32-byte AES-256 key             │ │
                    │  │      (zeroed on Lock/Dispose)        │ │
                    │  │                                      │ │
                    │  │  VaultEntry[] (decrypted metadata)   │ │
                    │  │  └── Per-entry payloads remain       │ │
                    │  │      encrypted until GetPayload()    │ │
                    │  └──────────────────────────────────────┘ │
                    │                    ▲                       │
                    │         Unlock()   │  Lock()              │
                    │         ──────►    │  ◄──────             │
                    │  SecureString ──►  │                      │
                    │  Argon2id(pw,salt) │                      │
                    │  ──► derived key   │                      │
                    │                    │                       │
                    └────────────────────┼───────────────────────┘
                                        │
                                        ▼
                    ┌───────────────────────────────────────────┐
                    │          vault.enc  (On-Disk)             │
                    │                                           │
                    │  ┌── Envelope Header (plaintext JSON) ──┐ │
                    │  │  FormatVersion: 1                     │ │
                    │  │  Kdf: "argon2id"                      │ │
                    │  │  KdfParams:                            │ │
                    │  │    MemoryKiB: 65536  (64 MiB)         │ │
                    │  │    Iterations: 3                       │ │
                    │  │    Parallelism: 4                      │ │
                    │  │  Salt: <16-byte random>               │ │
                    │  │  Nonce: <12-byte AES-GCM nonce>       │ │
                    │  │  Tag: <16-byte AES-GCM auth tag>      │ │
                    │  └───────────────────────────────────────┘ │
                    │                                           │
                    │  ┌── Ciphertext (AES-256-GCM) ──────────┐ │
                    │  │  Encrypted VaultEntry[] JSON           │ │
                    │  │  Each entry contains:                  │ │
                    │  │    Id, Label, Type, Username            │ │
                    │  │    EncryptedPayload ◄── inner AES-GCM │ │
                    │  │    EntryNonce, EntryTag                │ │
                    │  │    AssociatedMachineIds                │ │
                    │  │    Timestamps                          │ │
                    │  └───────────────────────────────────────┘ │
                    └───────────────────────────────────────────┘
```

### 3.2 Cryptographic Specifications

| Parameter | Value | Rationale |
|---|---|---|
| **Encryption algorithm** | AES-256-GCM | AEAD — provides confidentiality + integrity + authenticity |
| **Key derivation** | Argon2id | Memory-hard KDF; resists GPU/ASIC brute-force |
| **Argon2id memory** | 64 MiB | Forces ~64 MiB RAM per guess, making parallel attacks expensive |
| **Argon2id iterations** | 3 | Balanced: ~400ms on modern desktop |
| **Argon2id parallelism** | 4 | Matches typical desktop CPU core count |
| **Salt** | 16 bytes (128-bit) | `RandomNumberGenerator.GetBytes(16)` — unique per vault |
| **Nonce** | 12 bytes (96-bit) | AES-GCM standard; fresh per encrypt operation, never reused |
| **Auth tag** | 16 bytes (128-bit) | AES-GCM integrity tag; detects any modification |
| **Key size** | 256 bits (32 bytes) | AES-256 security level |
| **HMAC chain key** | 32 bytes | Stored as a vault entry (encrypted); used for audit chain |

### 3.3 Key Lifecycle

```
┌─────────────┐     Unlock(pw)      ┌──────────────┐
│   SEALED    │ ───────────────────► │  UNLOCKED    │
│             │                      │              │
│ No key in   │     Lock()           │ Key in       │
│ memory      │ ◄─────────────────── │ pinned RAM   │
│             │                      │              │
│ All ops     │     Auto-lock after  │ All ops      │
│ rejected    │     15 min idle      │ permitted    │
└─────────────┘                      └──────────────┘
                                          │
                                     GetPayload(id)
                                          │
                                          ▼
                                   CredentialPayload
                                   ┌──────────────────┐
                                   │ IDisposable       │
                                   │ GCHandle.Pinned   │
                                   │ ReadOnlySpan<byte>│
                                   └────────┬─────────┘
                                            │ using block ends
                                            ▼
                                   ZeroMemory() + Free()
```

### 3.4 Credential Security Rules

| Rule | Enforcement |
|---|---|
| **R1:** No credential payload persists in plaintext on disk | Vault file is always encrypted; temp files use `FileOptions.DeleteOnClose` |
| **R2:** Decrypted credentials are pinned and zeroed | `GCHandle.Alloc(Pinned)` prevents GC relocation; `CryptographicOperations.ZeroMemory()` on `Dispose()` |
| **R3:** Credential access is individually audited | Every `GetPayloadAsync()` call emits `AuditAction.CredentialAccessed` with target machine context |
| **R4:** Master password never stored | Consumed once during Argon2id derivation; `SecureString` dereferenced immediately |
| **R5:** Per-entry inner encryption | Even with the outer key, individual credential payloads have their own AES-GCM nonce/tag — bulk decryption never happens |
| **R6:** Fresh nonce per encrypt | `RandomNumberGenerator.GetBytes(12)` for every encryption — nonce reuse is catastrophic for GCM and is prevented structurally |
| **R7:** Failed unlock throttling | Exponential backoff: 1s, 2s, 4s, 8s, 16s after 3 failures; lockout after 10 consecutive failures (requires app restart) |
| **R8:** Vault file ACLs enforced on creation | `chmod 600` (Linux) or NTFS ACL restricting to current user (Windows) |

### 3.5 Credential Types & Handling

| Type | Storage | Transport | Memory Lifetime |
|---|---|---|---|
| SSH Key (private key bytes) | AES-256-GCM in vault | Passed to SSH.NET in memory; never written to temp file | Duration of SSH session establishment |
| SSH Key + Passphrase | Key + passphrase both encrypted separately | Passphrase decrypts key in memory | Until SSH session established |
| Password | AES-256-GCM in vault | Piped to SSH stdin (sudo) or WinRM auth layer | Until connection authenticated |
| Kerberos | Ticket handled by OS credential manager | OS manages transport | OS-managed lifecycle |

---

## 4. Secure Communication Design

### 4.1 Protocol Security Matrix

| Protocol | Transport Encryption | Authentication | Integrity | Key Exchange | Status |
|---|---|---|---|---|---|
| **SSH** | ChaCha20-Poly1305 or AES-256-GCM | Public key (preferred), password | MAC per-packet | ECDH (curve25519) | ✅ Mature |
| **WinRM/HTTPS** | TLS 1.2+ (enforce 1.3) | Kerberos (preferred), NTLM, Basic+TLS | TLS record MAC | ECDHE | ✅ Mature |
| **gRPC Agent** | TLS 1.3 (mutual) | mTLS — X.509 client+server certs | TLS AEAD | ECDHE | ✅ Designed |
| **SQLite** | N/A (local file) | File system ACLs | HMAC chain (audit) | N/A | ⚠️ ACLs needed |

### 4.2 SSH Security Configuration

```
Required SSH Configuration (enforced by SshTransportProvider):

  ┌──────────────────────────────────────────────────────┐
  │  Host Key Verification: STRICT                        │
  │  ├── First connect: prompt user to accept fingerprint │
  │  ├── Store in ~/.homemanagement/known_hosts           │
  │  ├── Subsequent: verify against stored fingerprint    │
  │  └── On mismatch: REJECT + log Error + alert user    │
  │                                                       │
  │  Preferred Key Exchange:                              │
  │    1. curve25519-sha256                               │
  │    2. ecdh-sha2-nistp384                              │
  │    3. ecdh-sha2-nistp256                              │
  │                                                       │
  │  Preferred Ciphers:                                   │
  │    1. chacha20-poly1305@openssh.com                   │
  │    2. aes256-gcm@openssh.com                          │
  │    3. aes128-gcm@openssh.com                          │
  │                                                       │
  │  Preferred MACs:                                      │
  │    1. hmac-sha2-512-etm@openssh.com                   │
  │    2. hmac-sha2-256-etm@openssh.com                   │
  │                                                       │
  │  Rejected (disabled):                                 │
  │    - diffie-hellman-group1-sha1                       │
  │    - ssh-dss, ssh-rsa (SHA-1)                         │
  │    - arcfour, 3des-cbc, blowfish                      │
  │    - hmac-md5, hmac-sha1                              │
  └──────────────────────────────────────────────────────┘
```

### 4.3 WinRM Security Configuration

```
Required WinRM Configuration:

  ┌─────────────────────────────────────────────────────┐
  │  Transport: HTTPS ONLY (port 5986)                   │
  │  ├── HTTP (port 5985) REJECTED                       │
  │  ├── Server certificate validation: REQUIRED         │
  │  └── Self-signed certs: accepted only when            │
  │      fingerprint is pre-registered in inventory      │
  │                                                      │
  │  Authentication Priority:                            │
  │    1. Kerberos (domain-joined machines)              │
  │    2. Certificate-based (non-domain)                 │
  │    3. NTLM (fallback, discouraged)                   │
  │    ✘ Basic auth: DISABLED (transmits base64 creds)  │
  │                                                      │
  │  Encryption: Always encrypted (even with NTLM)      │
  │  ├── EnvelopeSize: 512 KB (limit command size)      │
  │  └── MaxMemoryPerShellMB: 1024                       │
  │                                                      │
  │  TLS Configuration:                                  │
  │    SslProtocols: Tls13 | Tls12                       │
  │    CipherSuitesPolicy: TLS_AES_256_GCM_SHA384,      │
  │                         TLS_AES_128_GCM_SHA256,      │
  │                         TLS_CHACHA20_POLY1305_SHA256  │
  └─────────────────────────────────────────────────────┘
```

### 4.4 gRPC Agent mTLS Configuration

```
Agent Certificate Architecture:

  ┌────────────────────────────────────────────────────────────────┐
  │                    Private Certificate Authority                 │
  │                                                                 │
  │  Control Machine (~/.homemanagement/certs/)                     │
  │  ├── ca.pfx ─── Private CA key + cert (ECDSA P-384)           │
  │  ├── ca.crt ─── CA public cert (distributed to agents)         │
  │  └── server.pfx ── Control server identity cert                │
  │      └── CN=homemanagement-control                              │
  │      └── SAN=DNS:mgmt.home.lan, IP:192.168.1.100              │
  │      └── Signed by: ca.pfx                                     │
  │      └── Validity: 1 year (auto-rotate reminder)               │
  │                                                                 │
  │  Agent Machine (/opt/hm-agent/certs/)                          │
  │  ├── agent.pfx ── Agent identity cert (ECDSA P-256)           │
  │  │   └── CN=hm-agent-{hostname}                                │
  │  │   └── Signed by: ca.pfx                                     │
  │  │   └── Validity: 1 year                                      │
  │  └── ca.crt ──── CA public cert (validates control server)    │
  │                                                                 │
  │  Mutual Authentication Flow:                                   │
  │  1. Agent connects to control:9444                             │
  │  2. TLS handshake begins                                       │
  │  3. Control presents server.pfx → Agent validates via ca.crt  │
  │  4. Agent presents agent.pfx → Control validates via ca.pfx   │
  │  5. Both verified → encrypted gRPC channel established         │
  │                                                                 │
  │  Certificate Revocation:                                       │
  │  - Control maintains a revoked-certs list (CRL file)          │
  │  - Agent cert serial checked against CRL on every connection   │
  │  - Revocation is immediate (no CRL caching by agents)          │
  └────────────────────────────────────────────────────────────────┘
```

### 4.5 Connection Pool Security

```
Pool Isolation Rules:

  ┌─────────────────────────────────────────────────────────────┐
  │  Pool Key = (Hostname, Port, CredentialId)                   │
  │                                                              │
  │  Rule 1: NEVER share connections across different            │
  │          CredentialIds (different privilege levels)           │
  │                                                              │
  │  Rule 2: Pooled connections are validated before reuse       │
  │          (send keepalive/ping; discard if stale)             │
  │                                                              │
  │  Rule 3: Max lifetime per connection = 30 minutes            │
  │          (forces re-authentication, limits token reuse)      │
  │                                                              │
  │  Rule 4: Idle timeout = 5 minutes                            │
  │          (reduce window for session hijacking)               │
  │                                                              │
  │  Rule 5: Max 3 connections per pool key                      │
  │          (per host:port:credential)                           │
  │                                                              │
  │  Rule 6: Global max 50 connections (all pools)               │
  │          (prevent resource exhaustion on control machine)     │
  └─────────────────────────────────────────────────────────────┘
```

### 4.6 TLS Protocol Enforcement

The following MUST be set at application startup before any network operations:

```csharp
// In AppBootstrapper.cs — called before any service resolution
AppContext.SetSwitch("System.Net.Security.UseManagedSni", true);

// For HttpClient-based transports (WinRM, gRPC)
// Configure via HttpClientHandler or SocketsHttpHandler:
handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;

// Reject older protocols explicitly
// TLS 1.0 and 1.1 are disabled by default in .NET 8, but make it explicit
AppContext.SetSwitch("System.Net.Security.AllowTls10Client", false);
AppContext.SetSwitch("System.Net.Security.AllowTls11Client", false);
```

---

## 5. Authentication & Authorization Model

### 5.1 Authentication Layers

```
┌─────────────────────────────────────────────────────────────────┐
│                    AUTHENTICATION ARCHITECTURE                    │
│                                                                  │
│  Layer 1: Application Access (Local Machine)                     │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  Prerequisite: OS user session (Windows login / su login)  │  │
│  │  → Application inherits OS user context                    │  │
│  │  → Actor identity = Environment.UserName                   │  │
│  │  → File access governed by OS file ACLs                    │  │
│  └────────────────────────────────────────────────────────────┘  │
│                         │                                        │
│  Layer 2: Vault Unlock (Defense-in-Depth)                        │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  Master password → Argon2id → AES-256 key                 │  │
│  │  → Controls access to all stored credentials               │  │
│  │  → Auto-locks after 15 min idle                            │  │
│  │  → Brute-force protection: backoff + lockout               │  │
│  │  → Audit: VaultUnlocked, VaultLocked events                │  │
│  └────────────────────────────────────────────────────────────┘  │
│                         │                                        │
│  Layer 3: Target Authentication (Per-Machine Credentials)        │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  Each machine references exactly ONE credential entry      │  │
│  │  ┌───────────────────────┬──────────────────────────────┐  │  │
│  │  │ Protocol              │ Auth Mechanism                │  │  │
│  │  ├───────────────────────┼──────────────────────────────┤  │  │
│  │  │ SSH                   │ Ed25519/RSA key (preferred)   │  │  │
│  │  │                       │ Password (fallback)           │  │  │
│  │  │ WinRM                 │ Kerberos (preferred)          │  │  │
│  │  │                       │ Certificate-based             │  │  │
│  │  │                       │ NTLM (last resort)            │  │  │
│  │  │ Agent (gRPC)          │ mTLS X.509 certificates       │  │  │
│  │  └───────────────────────┴──────────────────────────────┘  │  │
│  └────────────────────────────────────────────────────────────┘  │
│                         │                                        │
│  Layer 4: Command-Level Authorization (Elevation Mode)           │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  ElevationMode.None      → run as connected user           │  │
│  │  ElevationMode.Sudo      → sudo <command>                  │  │
│  │  ElevationMode.SudoAsUser → sudo -u <user> <command>       │  │
│  │  ElevationMode.RunAsAdmin → PowerShell Start-Process -Verb │  │
│  │                              RunAs                          │  │
│  │                                                             │  │
│  │  Elevation is EXPLICIT per RemoteCommand — never implicit   │  │
│  └────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

### 5.2 Authorization Model

#### v1: Single-Operator (Current)

```
┌──────────────────────────────────────────────────────┐
│  v1 Trust Model: "Castle and Moat"                    │
│                                                       │
│  Guard: OS login + vault master password              │
│  Once inside: full access to all operations           │
│                                                       │
│  Assumptions:                                        │
│  - Single trusted operator                            │
│  - Control machine physically secured                 │
│  - LAN environment (not internet-facing)             │
│  - Operator is accountable for all actions            │
└──────────────────────────────────────────────────────┘
```

#### v2: Role-Based Access Control (Future-Ready)

The interface design supports RBAC without refactoring:

```
┌──────────────────────────────────────────────────────────────────┐
│  v2 RBAC Model (when multi-user API server is added)             │
│                                                                  │
│  Roles:                                                          │
│  ┌────────────┬──────────────────────────────────────────────┐   │
│  │ Role       │ Permissions                                  │   │
│  ├────────────┼──────────────────────────────────────────────┤   │
│  │ Admin      │ Full access: vault, settings, all machines   │   │
│  │ Operator   │ Execute: patch, service control              │   │
│  │            │ Scoped to: assigned machine groups            │   │
│  │            │ Denied: vault management, settings            │   │
│  │ Viewer     │ Read-only: inventory, audit, job status      │   │
│  │            │ Denied: any state-changing operation          │   │
│  │ Auditor    │ Read: audit logs, export                     │   │
│  │            │ Denied: all operational actions               │   │
│  └────────────┴──────────────────────────────────────────────┘   │
│                                                                  │
│  Enforcement Points:                                             │
│  - IAuditLogger.RecordAsync() — always records actor identity    │
│  - IJobScheduler.SubmitAsync() — can check role before execution │
│  - ICredentialVault — restricted to Admin role                   │
│  - MachineQuery — can filter by role-permitted machine groups    │
│                                                                  │
│  Token Format (future): JWT with role claims                     │
│  Token Lifetime: 1 hour (refresh via sliding window)             │
│  Token Storage: HTTP-only secure cookie (web) or OS keychain     │
└──────────────────────────────────────────────────────────────────┘
```

### 5.3 Session Security

| Control | v1 Implementation | Purpose |
|---|---|---|
| Vault auto-lock | 15-minute idle timer → `ICredentialVault.LockAsync()` | Limits exposure window on unattended workstation |
| Session timeout (target) | 5-minute idle → connection closed, returned to pool | Limits window for session hijacking |
| Connection max lifetime | 30 minutes → forced re-authentication | Ensures credential validity |
| Correlation scope | New `CorrelationId` per user action; `AsyncLocal<T>` propagation | Non-spoofable action attribution |
| Clipboard auto-clear | 30 seconds after credential copy | Prevents clipboard scraping |

---

## 6. OS Hardening Recommendations

### 6.1 Control Machine — Windows

```
┌──────────────────────────────────────────────────────────────────┐
│  WINDOWS CONTROL MACHINE HARDENING                                │
│                                                                   │
│  File System Protection:                                         │
│  ├── vault.enc: NTFS ACL → Owner: current user, deny all others │
│  ├── homemanagement.db: same ACL                                 │
│  ├── certs/: same ACL (contains CA private key)                  │
│  ├── known_hosts: same ACL                                       │
│  └── logs/: current user + Administrators (for diagnostics)      │
│                                                                   │
│  Network:                                                        │
│  ├── Windows Firewall: inbound 9444/tcp (gRPC) from LAN only    │
│  ├── Outbound: 22/tcp (SSH), 5986/tcp (WinRM) to managed hosts  │
│  └── Block all other inbound to HomeManagement process           │
│                                                                   │
│  Process:                                                        │
│  ├── Run as standard user (NOT administrator)                     │
│  ├── Enable DEP (Data Execution Prevention) — default in .NET 8  │
│  ├── Enable ASLR (Address Space Layout Randomization) — default  │
│  ├── Disable core dumps containing credential memory:            │
│  │   HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting     │
│  │     DontSendAdditionalData = 1                                │
│  └── Enable Windows Credential Guard (if Hyper-V available)      │
│                                                                   │
│  Authentication:                                                 │
│  ├── Strong password/PIN on OS account                           │
│  ├── Enable Windows Hello or smart card                          │
│  └── Screen lock timeout ≤ 5 minutes                             │
│                                                                   │
│  Updates:                                                        │
│  ├── Windows Update: automatic, no deferral for security patches │
│  └── .NET 8 runtime: check for updates monthly                   │
│                                                                   │
│  Logging:                                                        │
│  ├── Enable Security audit policy (logon events, object access)  │
│  └── Forward Windows Event Log to SIEM (if available)            │
└──────────────────────────────────────────────────────────────────┘
```

### 6.2 Control Machine — Linux

```
┌──────────────────────────────────────────────────────────────────┐
│  LINUX CONTROL MACHINE HARDENING                                  │
│                                                                   │
│  File System Protection:                                         │
│  ├── chmod 700 ~/.homemanagement/                                │
│  ├── chmod 600 ~/.homemanagement/vault.enc                       │
│  ├── chmod 600 ~/.homemanagement/homemanagement.db               │
│  ├── chmod 700 ~/.homemanagement/certs/                          │
│  ├── chmod 600 ~/.homemanagement/certs/*.pfx                     │
│  ├── chmod 644 ~/.homemanagement/certs/ca.crt                    │
│  ├── chmod 600 ~/.homemanagement/known_hosts                     │
│  └── Consider: mount ~/.homemanagement as tmpfs+encrypted        │
│                                                                   │
│  Network:                                                        │
│  ├── iptables/nftables: allow IN 9444/tcp from LAN CIDR only    │
│  ├── Allow OUT 22/tcp, 5986/tcp to managed host ranges           │
│  └── Default deny all other in/out for HomeManagement ports      │
│                                                                   │
│  Process:                                                        │
│  ├── Run as dedicated non-root user (e.g., hm-operator)         │
│  ├── No SUID bits on binary                                      │
│  ├── Use systemd hardening (if running as service):              │
│  │   ProtectSystem=strict                                        │
│  │   ProtectHome=read-only                                       │
│  │   ReadWritePaths=~/.homemanagement                            │
│  │   PrivateTmp=yes                                              │
│  │   NoNewPrivileges=yes                                         │
│  │   CapabilityBoundingSet=                                      │
│  │   MemoryDenyWriteExecute=yes                                  │
│  └── Set ulimit: open files 1024, max processes 256              │
│                                                                   │
│  Kernel:                                                         │
│  ├── Disable core dumps: echo 0 > /proc/sys/kernel/core_pattern │
│  ├── Set kernel.yama.ptrace_scope = 2 (no ptrace by non-root)   │
│  └── Set vm.swappiness = 1 (minimize credential swap to disk)    │
│                                                                   │
│  Updates:                                                        │
│  ├── unattended-upgrades (Debian/Ubuntu) or dnf-automatic (RHEL) │
│  └── .NET 8 runtime: check for updates via dotnet-install script │
└──────────────────────────────────────────────────────────────────┘
```

### 6.3 Managed Target — Linux (SSH)

```
┌──────────────────────────────────────────────────────────────────┐
│  LINUX TARGET HARDENING                                           │
│                                                                   │
│  SSH Configuration (/etc/ssh/sshd_config):                       │
│  ├── PermitRootLogin no                                          │
│  ├── PasswordAuthentication no  (prefer key-based)               │
│  ├── PubkeyAuthentication yes                                    │
│  ├── MaxAuthTries 3                                              │
│  ├── ClientAliveInterval 300                                     │
│  ├── ClientAliveCountMax 2                                       │
│  ├── AllowUsers hm-operator  (restrict to dedicated user)        │
│  ├── KexAlgorithms curve25519-sha256,ecdh-sha2-nistp384          │
│  ├── Ciphers chacha20-poly1305@openssh.com,aes256-gcm@openssh   │
│  └── MACs hmac-sha2-512-etm@openssh.com,hmac-sha2-256-etm       │
│                                                                   │
│  Dedicated Service Account (hm-operator):                        │
│  ├── Locked password (no interactive login)                      │
│  ├── SSH key-only authentication                                 │
│  ├── sudoers entry (limited):                                    │
│  │   hm-operator ALL=(ALL) NOPASSWD: /usr/bin/apt,              │
│  │     /usr/bin/dnf, /usr/bin/yum,                               │
│  │     /usr/bin/systemctl start *, /usr/bin/systemctl stop *,    │
│  │     /usr/bin/systemctl restart *, /usr/bin/systemctl status * │
│  │   # NO: /bin/bash, /usr/bin/su, /usr/bin/passwd              │
│  └── Home directory: /home/hm-operator (restricted)              │
│                                                                   │
│  Monitoring:                                                     │
│  ├── auditd rules for sudo usage by hm-operator                 │
│  └── fail2ban on SSH (ban after 5 failures)                      │
└──────────────────────────────────────────────────────────────────┘
```

### 6.4 Managed Target — Windows (WinRM)

```
┌──────────────────────────────────────────────────────────────────┐
│  WINDOWS TARGET HARDENING                                         │
│                                                                   │
│  WinRM Configuration:                                            │
│  ├── Enable HTTPS listener only:                                 │
│  │   winrm create winrm/config/Listener?Address=*+Transport=HTTPS│
│  ├── Delete HTTP listener:                                       │
│  │   winrm delete winrm/config/Listener?Address=*+Transport=HTTP │
│  ├── Set: AllowUnencrypted = false                               │
│  ├── Set: MaxConcurrentOperationsPerUser = 15                    │
│  ├── Set: MaxMemoryPerShellMB = 1024                             │
│  └── Certificate: issued by trusted CA (not self-signed in prod) │
│                                                                   │
│  Dedicated Service Account:                                      │
│  ├── Create local user: hm-operator                              │
│  ├── Group memberships:                                          │
│  │   ✔ Remote Management Users (WinRM access)                   │
│  │   ✔ Performance Monitor Users (if hardware info needed)       │
│  │   ✘ Administrators (NOT a member; use JEA for elevation)      │
│  ├── Just Enough Administration (JEA) endpoint:                  │
│  │   Expose only: Get-WindowsUpdate, Install-WindowsUpdate,     │
│  │   Get-Service, Start-Service, Stop-Service, Restart-Service,  │
│  │   Get-CimInstance (Win32_OperatingSystem, Win32_Processor)    │
│  │   Deny: Invoke-Expression, Start-Process, New-PSSession      │
│  └── Account lockout: 5 failed attempts → 30 min lockout        │
│                                                                   │
│  Windows Firewall:                                               │
│  ├── Allow inbound 5986/tcp from control machine IP ONLY        │
│  └── Block 5985/tcp (HTTP WinRM) inbound                         │
│                                                                   │
│  Event Logging:                                                  │
│  ├── Enable WinRM operational log                                │
│  ├── Enable PowerShell ScriptBlock logging                       │
│  └── Forward to SIEM via Windows Event Forwarding                │
└──────────────────────────────────────────────────────────────────┘
```

### 6.5 Agent Target Hardening

```
┌──────────────────────────────────────────────────────────────────┐
│  AGENT TARGET HARDENING (Linux + Windows)                         │
│                                                                   │
│  Service Account:                                                │
│  ├── Linux: dedicated user "hm-agent", no login shell            │
│  │   useradd -r -s /usr/sbin/nologin hm-agent                   │
│  ├── Windows: "NT SERVICE\HMAgent" (virtual service account)     │
│  └── Minimal privileges: runs as low-priv by default             │
│                                                                   │
│  Elevation:                                                      │
│  ├── Agent receives ElevationMode per command                     │
│  ├── Sudo/RunAs performed only when command explicitly requires  │
│  ├── Sudoers entry: same limited command set as agentless mode   │
│  └── Agent REJECTS commands requesting unrestricted shell access │
│                                                                   │
│  Binary Protection:                                              │
│  ├── Read-only binary directory                                  │
│  │   Linux: /opt/hm-agent/ owned by root, 755                   │
│  │   Windows: C:\ProgramData\HMAgent\ ACL: SYSTEM + Admins      │
│  ├── Agent binary: owned by root/SYSTEM, not writable by agent   │
│  └── Config file: readable by agent user, writable by root only  │
│                                                                   │
│  Certificate Protection:                                         │
│  ├── agent.pfx: chmod 600, owned by hm-agent user               │
│  ├── ca.crt: chmod 644 (public)                                  │
│  └── Private key never leaves the target machine                 │
│                                                                   │
│  Network:                                                        │
│  ├── Agent makes OUTBOUND connection to control (no inbound)     │
│  ├── Firewall: no inbound rule needed (agent initiates)          │
│  └── Reconnect with exponential backoff on connection loss       │
│                                                                   │
│  Self-Protection:                                                │
│  ├── Reject commands from unauthenticated connections            │
│  ├── Rate limit: max 10 commands/second (DoS protection)         │
│  ├── Command allowlist filtering (future: configurable)          │
│  └── Watchdog: auto-restart on crash (systemd/Windows SCM)       │
└──────────────────────────────────────────────────────────────────┘
```

---

## 7. Logging & Audit Requirements

### 7.1 Audit Event Requirements (Mandatory)

Every auditable action MUST produce an `AuditEvent` with ALL of these fields:

| Field | Requirement | Source |
|---|---|---|
| `EventId` | Globally unique, non-sequential GUID | `Guid.NewGuid()` |
| `TimestampUtc` | UTC timestamp, millisecond precision | `DateTime.UtcNow` |
| `CorrelationId` | Links all events from a single user action | `ICorrelationContext.CorrelationId` |
| `Action` | Enum identifying the operation | `AuditAction.XxxCompleted` |
| `ActorIdentity` | OS username of the operator | `Environment.UserName` |
| `TargetMachineId` | Machine affected (if applicable) | Operation parameter |
| `TargetMachineName` | Machine hostname (for readability) | Operation parameter |
| `Detail` | Human-readable description, **redacted** | `ISensitiveDataFilter.Redact(detail)` |
| `Properties` | Structured metadata dict, **redacted** | `ISensitiveDataFilter.RedactProperties(props)` |
| `Outcome` | `Success`, `Failure`, or `PartialSuccess` | Operation result |
| `ErrorMessage` | Error details on failure (redacted) | Exception message |

### 7.2 Mandatory Audit Events by Subsystem

#### Credential Vault
| Action | When | Detail Contents |
|---|---|---|
| `VaultUnlocked` | Successful unlock | KDF timing (ms) |
| `VaultLocked` | Manual lock or auto-lock | Lock reason (manual/idle/shutdown) |
| `CredentialCreated` | New credential added | Label, type, associated machines (NOT the payload) |
| `CredentialUpdated` | Credential modified | Changed fields (NOT the payload) |
| `CredentialDeleted` | Credential removed | Label, type |
| `CredentialAccessed` | `GetPayloadAsync()` called | Target machine being authenticated, credential label |

#### Patch Manager
| Action | When | Detail Contents |
|---|---|---|
| `PatchScanStarted` | Scan initiated | Machine count, scan type |
| `PatchScanCompleted` | Scan finished | Patches found count, severity breakdown |
| `PatchApproved` | User approves patches | Patch IDs, target machines |
| `PatchInstallStarted` | Apply begins | Batch size, options (DryRun, AllowReboot) |
| `PatchInstallCompleted` | Apply succeeds | Success/fail counts, reboot required |
| `PatchInstallFailed` | Apply fails | Error messages, failed patch IDs |

#### Service Controller
| Action | When | Detail Contents |
|---|---|---|
| `ServiceStarted` | Start action succeeds | Service name, machine, resulting state |
| `ServiceStopped` | Stop action succeeds | Service name, machine, resulting state |
| `ServiceRestarted` | Restart action succeeds | Service name, machine, duration |

#### Machine Inventory
| Action | When | Detail Contents |
|---|---|---|
| `MachineAdded` | Machine registered | Hostname, OS, protocol |
| `MachineRemoved` | Machine soft-deleted | Hostname, removal reason |
| `MachineMetadataRefreshed` | Metadata updated | Changed fields |

#### Agent Gateway
| Action | When | Detail Contents |
|---|---|---|
| `AgentConnected` | Agent establishes connection | Agent ID, hostname, version, cert serial |
| `AgentDisconnected` | Agent disconnects | Agent ID, reason |
| `AgentUpdated` | Agent binary updated | Agent ID, old version, new version, hash |

#### Orchestration
| Action | When | Detail Contents |
|---|---|---|
| `JobSubmitted` | Job created | Job type, target count, submitter |
| `JobCompleted` | Job finishes | Success/fail counts, duration |
| `JobFailed` | Job fails entirely | Error message, targets affected |
| `JobCancelled` | Job cancelled by user | Job ID, reason |

### 7.3 HMAC Chain Integrity Requirements

```
Chain Requirements:

  1. SERIALIZATION: Canonical JSON — deterministic key ordering, no
     whitespace, UTC dates in ISO 8601, properties alphabetically sorted.
     
     Example canonical payload:
     {"action":"PatchInstallCompleted","actorIdentity":"jdoe",
      "detail":"3 patches installed","eventId":"a1b2c3...",
      "machineId":"d4e5f6...","outcome":"Success",
      "timestampUtc":"2026-03-14T10:30:00.000Z"}

  2. HASH FUNCTION: HMAC-SHA256
     Key: 32-byte random, stored as a credential in the vault
     Input: canonical_payload + previous_event_hash
     Output: 64-char hex string

  3. CHAIN ORDER: Strict sequential by TimestampUtc + EventId
     Concurrent writes serialized via SemaphoreSlim(1)

  4. FIRST EVENT: PreviousHash = "" (empty string)
     EventHash = HMAC-SHA256(key, canonical_payload + "")

  5. VERIFICATION: ChainVerifier walks from event[0] to event[N],
     recomputing each hash. Mismatch → tampering detected.
     Must run against full chain (no partial verification).

  6. KEY ROTATION: When HMAC key is rotated:
     - New events use new key
     - Rotation event recorded with both old-key hash and new-key hash
     - Verifier switches keys at rotation boundary

  7. EXTERNAL CHECKPOINT: Administrator SHOULD record the latest
     EventHash externally (e.g., printed, emailed, saved to separate
     storage) periodically for out-of-band verification.
```

### 7.4 Log Redaction Requirements

The `ISensitiveDataFilter` implementation MUST redact these patterns:

| Pattern | Detection | Replacement |
|---|---|---|
| Passwords | Key contains `password`, `passwd`, `pwd`, `secret` (case-insensitive) | `"***REDACTED***"` |
| Private keys | `-----BEGIN.*PRIVATE KEY-----` | `"***PRIVATE_KEY_REDACTED***"` |
| Tokens/API keys | Key contains `token`, `apikey`, `api_key`, `bearer` | `"***REDACTED***"` |
| Connection strings | Contains `Password=` or `pwd=` | Replace value after `=` with `***` |
| Base64 credentials | Matches `Basic [A-Za-z0-9+/=]{20,}` | `"Basic ***REDACTED***"` |
| SSH key fingerprints | `SHA256:...` (these are OK to log) | **Do NOT redact** (fingerprints are public) |

### 7.5 Log Retention & Protection

| Log Type | Retention | Protection | Archival |
|---|---|---|---|
| Application log (Serilog file) | 30 days rolling | File ACLs (owner only) | Auto-delete after retention |
| Console output | Session only | N/A (ephemeral) | None |
| Audit events (SQLite) | Indefinite | HMAC chain + file ACLs | Monthly export recommended |
| Agent logs (on target) | 14 days rolling | Agent user owns, root can read | Auto-delete after retention |

---

## 8. Secure Update & Patching Workflow

### 8.1 Self-Update Workflow (Control Application)

```
┌─────────────────────────────────────────────────────────────────┐
│  CONTROL APPLICATION UPDATE                                      │
│                                                                  │
│  1. CHECK: Application queries update manifest (signed JSON)    │
│     ├── Manifest URL: configurable (default: local file/share)  │
│     ├── Manifest signed with Ed25519 key                        │
│     └── Contains: version, SHA-256 hash, download URL, changelog│
│                                                                  │
│  2. VERIFY:                                                     │
│     ├── Verify manifest signature with embedded public key      │
│     ├── Compare version > current version                        │
│     └── Display changelog to operator for approval              │
│                                                                  │
│  3. DOWNLOAD:                                                    │
│     ├── Download binary to temp directory                        │
│     ├── Compute SHA-256 of downloaded file                       │
│     └── Compare against manifest hash (MUST match)              │
│                                                                  │
│  4. APPLY:                                                       │
│     ├── Lock vault (persist state)                               │
│     ├── Backup current binary                                    │
│     ├── Replace binary (Windows: rename-on-reboot if locked)     │
│     └── Prompt operator to restart application                   │
│                                                                  │
│  5. AUDIT: Record SettingsChanged event with old/new version    │
│                                                                  │
│  SECURITY CONTROLS:                                              │
│  ├── NO auto-update — operator must approve                     │
│  ├── Ed25519 signature verification (not just SHA-256)          │
│  ├── TLS for download (when remote)                             │
│  ├── No code execution from update path (download, verify, copy)│
│  └── Rollback: keep previous binary for manual restore          │
└─────────────────────────────────────────────────────────────────┘
```

### 8.2 Agent Update Workflow

```
┌─────────────────────────────────────────────────────────────────┐
│  AGENT BINARY UPDATE (Push from Control)                         │
│                                                                  │
│  1. PREPARE (on Control Machine):                                │
│     ├── Operator selects agent(s) to update                     │
│     ├── Build AgentUpdatePackage:                                │
│     │   ├── Version: "2.1.0"                                    │
│     │   ├── BinarySha256: computed SHA-256 of new binary        │
│     │   └── DownloadUrl: file server or inline transfer         │
│     └── Binary must be signed:                                   │
│           Windows: Authenticode signature                        │
│           Linux: detached GPG signature (.sig file)              │
│                                                                  │
│  2. TRANSFER (Control → Agent via gRPC mTLS):                   │
│     ├── IAgentGateway.RequestUpdateAsync(agentId, package)      │
│     ├── Binary transferred over existing mTLS channel            │
│     └── Agent receives package + signature                       │
│                                                                  │
│  3. VERIFY (on Agent):                                           │
│     ├── Compute SHA-256 of received binary                       │
│     ├── Compare against BinarySha256 in package (MUST match)    │
│     ├── Verify code signature:                                   │
│     │   Windows: Authenticode chain → trusted publisher          │
│     │   Linux: GPG verify against embedded public key            │
│     └── On ANY verification failure:                             │
│           ├── REJECT update                                      │
│           ├── Delete downloaded binary                           │
│           ├── Log Error on agent                                 │
│           └── Report failure to control via gRPC                 │
│                                                                  │
│  4. APPLY (on Agent):                                            │
│     ├── Stage new binary in update/ subdirectory                 │
│     ├── Signal watchdog/service manager for graceful restart     │
│     ├── Watchdog:                                                │
│     │   a. Stop agent process                                    │
│     │   b. Replace binary                                        │
│     │   c. Start agent process                                   │
│     │   d. Health check: agent reconnects within 60 seconds     │
│     │   e. If health check fails: rollback to previous binary   │
│     └── Report new version to control after reconnect            │
│                                                                  │
│  5. AUDIT:                                                       │
│     ├── Control: AgentUpdated event (agentId, old→new version)  │
│     └── Agent: local log entry with version transition           │
│                                                                  │
│  ROLLBACK:                                                       │
│  ├── Previous binary kept in backup/ directory                   │
│  ├── If new binary fails health check → auto-rollback            │
│  └── Manual rollback: operator triggers via control GUI          │
└─────────────────────────────────────────────────────────────────┘
```

### 8.3 Managed Target Patching Workflow (Security Gates)

```
┌─────────────────────────────────────────────────────────────────┐
│  SECURE PATCH APPLICATION WORKFLOW                                │
│                                                                  │
│  Gate 1: DETECTION (Read-Only)                                   │
│  ├── IPatchService.DetectAsync(target)                           │
│  ├── Commands: read-only queries (apt list, Get-WindowsUpdate)  │
│  ├── No elevation required for detection on most platforms       │
│  ├── Result: PatchInfo[] (read-only data, no state change)      │
│  └── Audit: PatchScanStarted → PatchScanCompleted               │
│                                                                  │
│  Gate 2: APPROVAL (Human Decision)                               │
│  ├── Operator reviews detected patches in GUI                    │
│  ├── Selects patches to apply                                    │
│  ├── Configures PatchOptions:                                    │
│  │   ├── DryRun: true/false                                     │
│  │   ├── AllowReboot: true/false                                │
│  │   ├── RebootDelay: TimeSpan                                   │
│  │   └── MaxConcurrentMachines: int                              │
│  └── Audit: PatchApproved (with selected patch IDs)             │
│                                                                  │
│  Gate 3: DRY RUN (Optional, Recommended)                        │
│  ├── IPatchService.ApplyAsync(target, patches, {DryRun: true})  │
│  ├── Simulates installation without making changes               │
│  ├── Reports expected outcomes                                   │
│  └── Operator reviews dry-run results before proceeding          │
│                                                                  │
│  Gate 4: APPLICATION (Elevated, State-Changing)                  │
│  ├── IPatchService.ApplyAsync(target, patches, options)          │
│  ├── Elevation: Sudo (Linux) / RunAsAdmin (Windows)             │
│  ├── Per-patch status tracking                                   │
│  ├── Progress events via IJobScheduler.ProgressStream            │
│  ├── On failure: stop batch (configurable: continue/stop)       │
│  └── Audit: PatchInstallStarted → per-patch outcomes →          │
│             PatchInstallCompleted/PatchInstallFailed              │
│                                                                  │
│  Gate 5: VERIFICATION (Read-Only)                                │
│  ├── IPatchService.VerifyAsync(target, patchIds)                │
│  ├── Re-scans target to confirm patches are installed            │
│  ├── Reports discrepancies (expected installed, but not found)   │
│  └── Audit: PatchScanCompleted (verification scan)              │
│                                                                  │
│  Gate 6: REBOOT (If Required, Controlled)                       │
│  ├── Only if PatchResult.RebootRequired && AllowReboot           │
│  ├── Reboot after RebootDelay (default: 0 = immediate)           │
│  ├── Machine state → Maintenance during reboot                   │
│  ├── Post-reboot connectivity check                              │
│  └── Machine state → Online after reconnection                   │
│                                                                  │
│  INVARIANTS:                                                     │
│  ├── No patch is applied without explicit operator approval     │
│  ├── No reboot without AllowReboot = true                       │
│  ├── Every state transition is audited                          │
│  ├── Credentials are fetched per-operation, never cached         │
│  ├── All commands use validated types (no injection possible)    │
│  └── Partial failures are atomically recorded per-patch          │
└─────────────────────────────────────────────────────────────────┘
```

---

## 9. Vulnerability Register & Remediation Plan

### 9.1 Critical Findings (P0 — Block Production Use)

| ID | Vulnerability | STRIDE | Component | Risk | Remediation |
|---|---|---|---|---|---|
| **V-001** | Agent binary integrity not verified before execution | **T** Tampering | Agent Gateway | Modified agent binary could execute arbitrary code on targets | Implement Authenticode (Windows) + GPG (Linux) signature verification in `IAgentGateway.RequestUpdateAsync()` flow. Agent MUST verify signature before replacing binary. |
| **V-002** | Vault brute-force protection not enforced | **S** Spoofing | Credential Vault | Unlimited unlock attempts against vault keyfile (offline) | Implement failed-attempt counter with exponential backoff (1s×2^n) and hard lockout after 10 consecutive failures requiring application restart. Persist failure count to prevent restart-and-retry. |
| **V-003** | Vault file ACLs not set by application | **I** Disclosure | Credential Vault | Other users on same machine can read vault.enc | On vault file creation: set `chmod 600` (Linux) or NTFS ACL (Windows, current user only). Verify ACLs on every `UnlockAsync()` call — warn if permissions are too open. |
| **V-004** | GUI vault auto-lock not implemented | **I** Disclosure | GUI Backend | Unattended workstation leaves vault unlocked indefinitely | Implement idle timer monitoring user input events; call `ICredentialVault.LockAsync()` after configurable timeout (default: 15 min). |

### 9.2 High Findings (P1 — Fix Before Sensitive Infrastructure)

| ID | Vulnerability | STRIDE | Component | Risk | Remediation |
|---|---|---|---|---|---|
| **V-005** | SSH host key management not exposed in GUI | **S** Spoofing | Transport / GUI | Users cannot verify or manage SSH fingerprints, risking MITM acceptance | Add host key fingerprint display in `MachineDetailViewModel`; prompt user to verify on first connection; show warning on change. |
| **V-006** | TLS version not explicitly enforced in code | **I** Disclosure | Transport | Falling back to TLS 1.0/1.1 in degraded environments | Set `AppContext` switches at startup to disable TLS 1.0/1.1; configure `SslProtocols.Tls13 | Tls12` on all `HttpClientHandler` instances. |
| **V-007** | WinRM NTLM downgrade possible | **I** Disclosure | Transport | NTLM hash could be captured if Kerberos fails | Transport provider should prefer Kerberos; log `Warning` when NTLM fallback occurs; allow config to require Kerberos-only. |
| **V-008** | No agent-side command rate limiting | **D** DoS | Agent | Compromised control could flood agent with commands | Implement agent-side rate limiter: max 10 commands/second, reject excess with `ResourceExhausted` gRPC status. |
| **V-009** | Database file not protected | **T** Tampering | Data | Direct SQLite modification bypasses application logic | Set file ACLs matching vault.enc. For audit table: HMAC chain detects tampering. For non-audit tables: consider SQLite encryption extension (SEE) or application-level checksums. |
| **V-010** | No certificate revocation checking | **S** Spoofing | Agent Gateway | Revoked agent certs still accepted | Implement CRL file on control machine; check agent cert serial against CRL on every mTLS handshake. |

### 9.3 Medium Findings (P2 — Address in Scheduled Maintenance)

| ID | Vulnerability | STRIDE | Component | Risk | Remediation |
|---|---|---|---|---|---|
| **V-011** | Audit chain key rotation not specified | **T** Tampering | Audit System | Cannot rotate HMAC key without breaking verification | Implement key rotation protocol: record rotation event with dual hashes (old key + new key); verifier switches keys at boundary. |
| **V-012** | No external audit checkpoint mechanism | **R** Repudiation | Audit System | Attacker who replaces entire DB file goes undetected | Provide `ExportChainCheckpoint()` → returns latest EventHash for external storage (email, print, separate system). |
| **V-013** | Core dump may contain credentials | **I** Disclosure | All | Process crash could write decrypted key material to core file | Disable core dumps: `kernel.core_pattern = ""` (Linux), `DontSendAdditionalData` (Windows). Add `Environment.FailFast` for fatal crypto errors. |
| **V-014** | No swap file protection | **I** Disclosure | Credential Vault | Pages containing decrypted credentials could be swapped to disk | Set `vm.swappiness=1` (already mitigated by `GCHandle.Pinned`); consider `mlock()` via P/Invoke for critical buffers. |
| **V-015** | Agent reconnect infinite retry | **D** DoS | Agent | Failed reconnection loop consumes resources | Implement capped exponential backoff: 1s → 30s → 60s max; raise alert after 10 consecutive failures. |

---

## 10. Security Testing Requirements

### 10.1 Unit Test Requirements

| Test Area | Test Cases | Priority |
|---|---|---|
| **Validated Types** | `Hostname.Create()` rejects shell metacharacters (`; \| & $ \` > <`), SQL injection (`' OR 1=1`), null bytes, empty, > 253 chars | P0 |
| **Validated Types** | `ServiceName.Create()` rejects wildcards (`*`), shell operators, empty, > 255 chars | P0 |
| **Validated Types** | `CidrRange.Create()` rejects invalid octets (>255), invalid prefix (>/32, <0), malformed input | P0 |
| **CredentialPayload** | `Dispose()` zeroes underlying byte array; `DecryptedPayload` throws `ObjectDisposedException` after dispose | P0 |
| **CredentialPayload** | GC does not relocate pinned buffer (stress test with GC.Collect) | P1 |
| **AES-GCM** | Round-trip: encrypt → decrypt produces identical plaintext | P0 |
| **AES-GCM** | Modified ciphertext → `CryptographicException` (tamper detection) | P0 |
| **AES-GCM** | Wrong key → `CryptographicException` | P0 |
| **AES-GCM** | Different nonces produce different ciphertext for same plaintext | P0 |
| **Argon2id** | Same password + salt → same derived key (deterministic) | P0 |
| **Argon2id** | Different password → different derived key | P0 |
| **HMAC Chain** | Three events chained correctly; verify passes | P0 |
| **HMAC Chain** | Modify middle event → verify detects break | P0 |
| **HMAC Chain** | Delete event from chain → verify detects gap | P0 |
| **SensitiveDataFilter** | Passwords, keys, tokens redacted from strings | P0 |
| **SensitiveDataFilter** | Non-sensitive data passes through unchanged | P0 |
| **ElevationHandler** | Sudo command does not contain injectable content | P0 |
| **ElevationHandler** | RunAs command does not contain injectable content | P0 |

### 10.2 Integration Test Requirements

| Test Area | Scenario | Priority |
|---|---|---|
| **Vault lifecycle** | Create vault → unlock → add credential → get payload → lock → verify zeroed → unlock → verify intact | P0 |
| **Vault rotation** | Add 3 credentials → rotate key → verify all 3 still accessible with new master password | P0 |
| **SSH transport** | Connect to test host → execute command → verify output → disconnect (Testcontainers SSH server) | P1 |
| **SSH host key** | First connect stores fingerprint → reconnect succeeds → change host key → connection REJECTED | P1 |
| **Audit chain** | Insert 100 events → verify chain → tamper event #50 → verify detects break at #50 | P0 |
| **Patch workflow** | Detect → approve → dry run → apply → verify (against test container) | P1 |
| **Connection pool** | Open max connections → next request queues → connection released → queued request proceeds | P1 |

### 10.3 Penetration Test Scenarios

| Scenario | Attack Vector | Expected Result |
|---|---|---|
| **Brute-force vault** | Automated rapid unlock attempts with wrong passwords | Exponential backoff → lockout after 10 attempts |
| **Modified vault file** | Flip one bit in vault.enc ciphertext | `CryptographicException` — AES-GCM tag mismatch |
| **Rogue SSH server** | DNS spoofing or ARP redirect to attacker SSH server | Connection REJECTED — host key mismatch |
| **Command injection via hostname** | Register machine with hostname `; rm -rf /` | `Hostname.Create()` throws — invalid characters |
| **Command injection via service name** | Control service `nginx; cat /etc/shadow` | `ServiceName.Create()` throws — invalid characters |
| **Audit trail tampering** | Direct SQL update of `AuditEvents` table | `ChainVerifier.VerifyAsync()` detects broken chain |
| **Agent impersonation** | Connect to control gRPC port with self-signed cert | mTLS handshake fails — cert not signed by CA |
| **Credential sniffing** | Packet capture on control ↔ target network | All protocols encrypted (SSH/TLS/mTLS) |
| **Memory dump analysis** | `procdump` on control application while vault unlocked | Key material in pinned buffer only; limited exposure window |
| **Vault file exfiltration** | Copy `vault.enc` to attacker machine, attempt offline brute-force | Argon2id 64 MiB memory cost → ~$10K+ to crack 8-char password via GPU |

---

## Appendix A: Security Architecture Diagram

```
 ┌──────────────────────────────────────────────────────────────────────────┐
 │                        SECURITY ARCHITECTURE                             │
 │                                                                          │
 │  ┌─────────────────────── DEFENSE LAYER 1: Access ──────────────────┐   │
 │  │  OS Authentication (login) → File ACLs → Process Isolation       │   │
 │  └──────────────────────────────────────────────────────────────────┘   │
 │                                    │                                     │
 │  ┌─────────────────────── DEFENSE LAYER 2: Vault ───────────────────┐   │
 │  │  Master Password → Argon2id → AES-256-GCM → Pinned+Zeroed Memory│   │
 │  │  Auto-lock on idle │ Brute-force throttling │ Audit on access    │   │
 │  └──────────────────────────────────────────────────────────────────┘   │
 │                                    │                                     │
 │  ┌─────────────────────── DEFENSE LAYER 3: Input ───────────────────┐   │
 │  │  Hostname (RFC 1123) │ ServiceName (allowlist) │ CidrRange (CIDR)│   │
 │  │  Compile-time type safety │ Parse-or-fail construction           │   │
 │  └──────────────────────────────────────────────────────────────────┘   │
 │                                    │                                     │
 │  ┌─────────────────────── DEFENSE LAYER 4: Transport ───────────────┐   │
 │  │  SSH (host key pinning)  │  WinRM (HTTPS/Kerberos)  │  mTLS     │   │
 │  │  Connection pool isolation │ Session timeout │ Circuit breaker    │   │
 │  └──────────────────────────────────────────────────────────────────┘   │
 │                                    │                                     │
 │  ┌─────────────────────── DEFENSE LAYER 5: Execution ───────────────┐   │
 │  │  Explicit ElevationMode │ Scoped sudoers/JEA │ No shell interp.  │   │
 │  │  Command templates │ Validated parameters only                    │   │
 │  └──────────────────────────────────────────────────────────────────┘   │
 │                                    │                                     │
 │  ┌─────────────────────── DEFENSE LAYER 6: Audit ───────────────────┐   │
 │  │  HMAC-SHA256 chain │ Append-only │ Correlation tracing            │   │
 │  │  Sensitive data redaction │ Serilog structured logging            │   │
 │  └──────────────────────────────────────────────────────────────────┘   │
 │                                    │                                     │
 │  ┌─────────────────────── DEFENSE LAYER 7: Resilience ──────────────┐   │
 │  │  Retry with backoff │ Circuit breaker │ Timeout enforcement       │   │
 │  │  Per-target isolation │ Global resource limits                    │   │
 │  └──────────────────────────────────────────────────────────────────┘   │
 │                                                                          │
 └──────────────────────────────────────────────────────────────────────────┘
```

## Appendix B: Compliance Mapping

| Requirement | CIS Control | Implementation |
|---|---|---|
| Encrypt sensitive data at rest | CIS 3.11 | AES-256-GCM vault |
| Encrypt sensitive data in transit | CIS 3.10 | SSH/TLS/mTLS |
| Maintain audit log | CIS 8.2 | HMAC-chained audit events |
| Use unique passwords | CIS 5.2 | Per-machine credentials in vault |
| Limit administrative privileges | CIS 5.4 | Explicit ElevationMode, scoped sudoers |
| Manage access control | CIS 6.1 | File ACLs, vault master password |
| Manage default accounts | CIS 6.3 | Dedicated hm-operator accounts on targets |
| Configure trusted channels | CIS 12.6 | SSH host key pinning, mTLS, HTTPS WinRM |
| Establish and maintain contact information | CIS 17.1 | Audit actor identity on all events |
