# Architecture Refinement — v1.1

## Overview

This document captures the architectural review findings and the concrete changes applied.
58 issues were identified across 8 categories. This revision addresses all critical and
high-severity items while preserving the original design intent.

---

## 1. Weaknesses Identified

### Critical (addressed in this revision)

| # | Category | Issue | Resolution |
|---|---|---|---|
| M1 | Modularity | Data layer directly referenced by 4 modules — no repository abstraction | Introduced `IRepository<T>` and per-module repository interfaces in Abstractions |
| M2 | Modularity | Agent project missing Abstractions reference | Added project reference |
| M3 | Modularity | Core references all implementations — no plugin model | Introduced `IModuleRegistration` self-registration pattern |
| S1 | Security | `CredentialPayload` is an immutable record — cannot zero memory | Changed to sealed `IDisposable` class with pinned buffer |
| S2 | Security | No input validation boundary types | Added `Hostname`, `ServiceName`, `CidrRange` validated value types |
| S3 | Security | Audit `Detail`/`Properties` can leak secrets — no enforcement | Added `ISensitiveDataFilter` interface + redaction contract |
| D1 | DI | ServiceRegistration has commented-out registrations | Each module now exposes `AddXxx(IServiceCollection)` extension |
| D2 | DI | Serilog bootstrapped as side-effect inside DI registration | Separated logging bootstrap from service registration |

### High (addressed in this revision)

| # | Category | Issue | Resolution |
|---|---|---|---|
| I1 | Interfaces | No `IAsyncEnumerable` for large result sets | Added streaming overloads for patch detection and service listing |
| I2 | Interfaces | No file transfer progress or checksum | Added `IProgress<TransferProgress>` parameter |
| I3 | Interfaces | Single global `IObservable` for job progress | Added per-job subscription method |
| I4 | Interfaces | `RemoteCommand.ElevatedExecution` is binary flag | Replaced with `ElevationMode` enum |
| DL1 | Data | Job results stored as JSON blob | New `JobMachineResultEntity` table with FK |
| DL2 | Data | Tags stored as JSON — not queryable | New `MachineTagEntity` join table |
| DL3 | Data | Missing indexes on compound query columns | Added composite indexes |
| DL4 | Data | No `UpdatedUtc` timestamp tracking | Added to all entities |
| DL5 | Data | No soft-delete support | Added `IsDeleted` + global query filter |
| CC1 | Cross-cutting | No `ICorrelationContext` | Added with `AsyncLocal<T>` backing |
| CC2 | Cross-cutting | No `IHealthCheck` | Added `ISystemHealthService` |
| CC3 | Cross-cutting | No circuit breaker abstraction | Added `ICircuitBreakerPolicy` |
| XP1 | Cross-platform | No OS detection on `TestConnection` | Returns `ConnectionTestResult` with detected OS |
| XP2 | Cross-platform | No path normalization abstraction | Added `IRemotePathResolver` |
| N1 | Naming | Models.cs is a 400-line mega-file | Split into per-domain files |

---

## 2. Revised Architecture Diagram

```
┌────────────────────────────────────────────────────────────────────────────┐
│                          LOCAL CONTROL MACHINE                             │
│                                                                            │
│  ┌──────────────────────────────────────────────────────────────────────┐  │
│  │                       GUI APPLICATION (Avalonia)                     │  │
│  │                                                                      │  │
│  │   Dashboard │ Machines │ Patching │ Services │ Jobs │ Audit │ Config │  │
│  │                                                                      │  │
│  └───────────────────────────┬──────────────────────────────────────────┘  │
│                              │ MVVM ViewModels                             │
│  ┌───────────────────────────┴──────────────────────────────────────────┐  │
│  │                       API BOUNDARY (Interfaces)                      │  │
│  │   IInventoryService · IPatchService · IServiceController             │  │
│  │   ICredentialVault · IJobScheduler · IAuditLogger                    │  │
│  └───────────────────────────┬──────────────────────────────────────────┘  │
│                              │                                             │
│  ┌───────────────────────────┴──────────────────────────────────────────┐  │
│  │                         CORE ENGINE                                  │  │
│  │                                                                      │  │
│  │  ┌────────────────┐  ┌────────────────┐  ┌───────────────────────┐  │  │
│  │  │  Orchestrator  │  │   Job Queue    │  │  Resilience Pipeline  │  │  │
│  │  │  (Scheduler)   │  │   (Channels)   │  │  ┌─────────────────┐  │  │  │
│  │  └───────┬────────┘  └───────┬────────┘  │  │ RetryPolicy     │  │  │  │
│  │          │                   │           │  │ CircuitBreaker   │  │  │  │
│  │          │                   │           │  │ Timeout          │  │  │  │
│  │          │                   │           │  └─────────────────┘  │  │  │
│  │          │                   │           └───────────────────────┘  │  │
│  │  ┌───────┴───────────────────┴─────────────────────────────────┐   │  │
│  │  │                     MODULE REGISTRY                          │   │  │
│  │  │  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌───────────────┐  │   │  │
│  │  │  │ Patching │ │ Service  │ │Inventory │ │ Credential    │  │   │  │
│  │  │  │ Module   │ │ Module   │ │ Module   │ │ Vault         │  │   │  │
│  │  │  └────┬─────┘ └────┬─────┘ └────┬─────┘ └───────┬───────┘  │   │  │
│  │  │       │             │            │               │          │   │  │
│  │  │  ┌────┴─────────────┴────────────┴───────────────┘          │   │  │
│  │  │  │  OS Strategy Layer                                       │   │  │
│  │  │  │  ┌─────────────┐  ┌──────────────┐  ┌────────────────┐  │   │  │
│  │  │  │  │ Linux       │  │ Windows      │  │ OS Detection   │  │   │  │
│  │  │  │  │ Strategies  │  │ Strategies   │  │ & Path Resolve │  │   │  │
│  │  │  │  │ (apt,dnf,   │  │ (PSUpdate,   │  │                │  │   │  │
│  │  │  │  │  systemctl)  │  │  sc.exe,     │  │                │  │   │  │
│  │  │  │  │             │  │  Get-Service) │  │                │  │   │  │
│  │  │  │  └─────────────┘  └──────────────┘  └────────────────┘  │   │  │
│  │  │  └─────────────────────────────────────────────────────────┘   │  │
│  │  └────────────────────────────────────────────────────────────────┘   │
│  │                              │                                        │
│  │  ┌───────────────────────────┴─────────────────────────────────────┐  │
│  │  │              TRANSPORT / REMOTE EXECUTION LAYER                  │  │
│  │  │                                                                  │  │
│  │  │  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌────────────────────┐ │  │
│  │  │  │   SSH    │ │  WinRM   │ │   PS     │ │  Agent Gateway     │ │  │
│  │  │  │ Provider │ │ Provider │ │ Remoting │ │  (gRPC + mTLS)     │ │  │
│  │  │  └──────────┘ └──────────┘ └──────────┘ └────────────────────┘ │  │
│  │  │                                                                  │  │
│  │  │  ┌───────────────────┐  ┌──────────────────────────────────┐   │  │
│  │  │  │ Connection Pool   │  │ Certificate Manager              │   │  │
│  │  │  │ & Circuit Breaker │  │ (mTLS, host key pinning)         │   │  │
│  │  │  └───────────────────┘  └──────────────────────────────────┘   │  │
│  │  └──────────────────────────────────────────────────────────────────┘  │
│  │                                                                       │
│  │  ┌──────────── CROSS-CUTTING INFRASTRUCTURE ────────────────────┐    │
│  │  │                                                               │    │
│  │  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────────────┐  │    │
│  │  │  │ Correlation  │ │ Sensitive    │ │ Health Check          │  │    │
│  │  │  │ Context      │ │ Data Filter  │ │ Service               │  │    │
│  │  │  │ (AsyncLocal) │ │ (Redaction)  │ │                       │  │    │
│  │  │  └──────────────┘ └──────────────┘ └──────────────────────┘  │    │
│  │  │                                                               │    │
│  │  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────────────┐  │    │
│  │  │  │ Input        │ │ Resilience   │ │ Configuration        │  │    │
│  │  │  │ Validation   │ │ (Retry +     │ │ Validation           │  │    │
│  │  │  │ Value Types  │ │ Breaker)     │ │                       │  │    │
│  │  │  └──────────────┘ └──────────────┘ └──────────────────────┘  │    │
│  │  └───────────────────────────────────────────────────────────────┘    │
│  │                                                                       │
│  │  ┌─────────────────────────┐  ┌───────────────────────────────────┐  │
│  │  │  Credential Vault       │  │  Audit Logger                     │  │
│  │  │  (AES-256-GCM + Argon2) │  │  (HMAC-chained, append-only)     │  │
│  │  │  IDisposable payloads   │  │  + sensitive data redaction       │  │
│  │  └─────────────────────────┘  └───────────────────────────────────┘  │
│  │                                                                       │
│  │  ┌────────────────────────────────────────────────────────────────┐  │
│  │  │               LOCAL DATA STORE (SQLite + EF Core)              │  │
│  │  │                                                                 │  │
│  │  │  Machines  │  Tags   │  PatchHistory  │  AuditEvents           │  │
│  │  │  Jobs      │  JobResults (normalized) │  ScheduledJobs         │  │
│  │  │  ── Soft-deletes ── Composite indexes ── UpdatedUtc tracking   │  │
│  │  └────────────────────────────────────────────────────────────────┘  │
│  └───────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────┘

                              │ Network │
          ┌───────────────────┼─────────┼────────────────────┐
          │                   │         │                    │
          ▼                   ▼         ▼                    ▼
   ┌─────────────┐   ┌─────────────┐  ┌─────────────┐ ┌──────────────┐
   │ Linux Host  │   │ Windows Host│  │ Linux Host  │ │ Windows Host │
   │ (SSH)       │   │ (WinRM)     │  │ (Agent)     │ │ (Agent)      │
   └─────────────┘   └─────────────┘  └─────────────┘ └──────────────┘
```

---

## 3. Revised Module Responsibilities

### 3.1 New: Cross-Cutting Infrastructure (`HomeManagement.Abstractions`)

The Abstractions assembly now contains cross-cutting contracts that all modules depend on:

| Concern | Interface | Purpose |
|---|---|---|
| Correlation | `ICorrelationContext` | Propagate correlation IDs via `AsyncLocal<T>` through all async calls |
| Health | `ISystemHealthService` | Aggregate health status of vault, DB, network, agents |
| Resilience | `IResiliencePipeline` | Compose retry + circuit-breaker + timeout into a single pipeline |
| Redaction | `ISensitiveDataFilter` | Filter secrets from log/audit free-text fields before persistence |
| Validation | Value types (`Hostname`, `ServiceName`, `CidrRange`) | Parse-or-fail construction prevents invalid data from propagating |
| DI Discovery | `IModuleRegistration` | Modules self-register their services without Core needing direct references |
| Repositories | `IRepository<T>` + per-domain interfaces | Decouple business modules from EF Core / Data project |

### 3.2 Revised: Credential Vault

- `CredentialPayload` → sealed class with `IDisposable`, pins sensitive bytes, zeros on dispose
- `ICredentialVault.GetPayloadAsync()` returns a disposable resource — caller wraps in `using`
- Vault operations emit audit events via `IAuditLogger` dependency (injected, not composed manually)

### 3.3 Revised: Transport Layer

- `IRemoteExecutor.TestConnectionAsync()` → returns `ConnectionTestResult` (not just `bool`) including detected OS, latency, protocol version
- `IRemoteExecutor.TransferFileAsync()` → accepts `IProgress<TransferProgress>` for progress reporting + SHA-256 verification
- New `IRemotePathResolver` handles OS-specific path normalization
- New `ICertificateManager` manages mTLS certs, host key pinning, certificate rotation
- `RemoteCommand.ElevatedExecution` → replaced with `ElevationMode` enum (`None`, `Sudo`, `SudoAsUser`, `RunAsAdmin`)

### 3.4 Revised: Data Layer

- Modules no longer reference `HomeManagement.Data` directly — they depend on repository interfaces in Abstractions
- New `MachineTagEntity` join table replaces JSON tags column
- New `JobMachineResultEntity` replaces JSON results blob in JobEntity
- All entities gain `UpdatedUtc` column
- `MachineEntity` gains `IsDeleted` + global query filter
- Composite indexes added for common query patterns
- `IpAddresses` uses EF Core value converter instead of raw JSON property

### 3.5 Revised: Core Composition

- `ServiceRegistration` no longer references implementation assemblies directly
- Each module exposes `public static IServiceCollection AddXyz(this IServiceCollection)` extension
- Core discovers and invokes these via `IModuleRegistration` pattern
- Serilog bootstrap separated into `LoggingBootstrap` class — called once at app startup
- DI container validates all registrations at startup

---

## 4. Key Design Decisions

### 4.1 Why validated value types instead of string validation attributes?

A `Hostname` that passes validation at construction can never become invalid. Every method accepting
`Hostname` gets a compile-time guarantee that the value was validated. This eliminates an entire
class of bugs where "someone forgot to call Validate()."

### 4.2 Why disposable credential payloads instead of immutable records?

Records cannot have their fields zeroed after construction. A `class` with `IDisposable` can:
- Pin the byte array with `GCHandle` to prevent GC relocation
- Zero the buffer in `Dispose()`
- Throw `ObjectDisposedException` if accessed after disposal

### 4.3 Why module self-registration instead of a central composition root?

When Core hardcodes `new PatchService()`, adding a new module requires editing Core. With
`IModuleRegistration`, each assembly registers itself. Core just discovers and invokes them.
This enables:
- Independent module testing without Core
- Optional modules (agent gateway only loaded if configured)
- Plugin architecture (future)

### 4.4 Why repository interfaces instead of direct DbContext access?

Direct `DbContext` access in business modules means:
- Every module must understand EF Core change tracking, LINQ translation limits
- Switching from SQLite to PostgreSQL requires touching every module
- Unit testing requires mocking DbContext (fragile, verbose)
- Repository interfaces allow in-memory fakes for testing
