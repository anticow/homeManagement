# HomeManagement — High-Level System Architecture

## 1. System Overview

HomeManagement is a cross-platform patching and service-controller system designed to manage Windows and Linux machines from a single local GUI application. It supports direct remote execution (agentless) and an optional lightweight agent model for environments where direct connectivity is restricted.

---

## 2. Architecture Diagram

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        LOCAL CONTROL MACHINE                             │
│                                                                          │
│  ┌─────────────────────────────────────────────────────────────────────┐ │
│  │                         GUI APPLICATION                             │ │
│  │  ┌──────────┐ ┌──────────┐ ┌───────────┐ ┌──────────┐ ┌─────────┐ │ │
│  │  │Dashboard │ │ Patch    │ │ Service   │ │Inventory │ │ Audit   │ │ │
│  │  │  View    │ │ Manager  │ │ Controller│ │ Browser  │ │ Log View│ │ │
│  │  └────┬─────┘ └────┬─────┘ └─────┬─────┘ └────┬─────┘ └────┬────┘ │ │
│  │       │             │             │             │            │       │ │
│  │  ─────┴─────────────┴─────────────┴─────────────┴────────────┴───── │ │
│  │                      GUI ↔ Core API Boundary                        │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
│                                    │                                     │
│  ┌─────────────────────────────────┴───────────────────────────────────┐ │
│  │                         CORE ENGINE                                  │ │
│  │                                                                      │ │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────────┐   │ │
│  │  │  Orchestrator │  │  Job Queue   │  │  Retry / Error Handler   │   │ │
│  │  │  (Scheduler)  │  │  & Executor  │  │  (Circuit Breaker)       │   │ │
│  │  └──────┬───────┘  └──────┬───────┘  └────────────┬─────────────┘   │ │
│  │         │                 │                        │                  │ │
│  │  ┌──────┴─────────────────┴────────────────────────┴──────────────┐  │ │
│  │  │                    COMMAND BROKER                               │  │ │
│  │  │  (Async dispatch, Channel<T> queue, result persistence,        │  │ │
│  │  │   fire-and-forget with CompletedStream for reactive updates)   │  │ │
│  │  └──────┬─────────────────┬────────────────────────┬──────────────┘  │ │
│  │         │                 │                        │                  │ │
│  │  ┌──────┴─────────────────┴────────────────────────┴──────────────┐  │ │
│  │  │                    MODULE REGISTRY                              │  │ │
│  │  │                                                                 │  │ │
│  │  │  ┌───────────────┐ ┌───────────────┐ ┌───────────────────────┐ │  │ │
│  │  │  │ Patch         │ │ Service       │ │ Inventory & Metadata  │ │  │ │
│  │  │  │ Detection &   │ │ Controller    │ │ Storage               │ │  │ │
│  │  │  │ Application   │ │ Module        │ │ Module                │ │  │ │
│  │  │  └───────┬───────┘ └───────┬───────┘ └───────────┬───────────┘ │  │ │
│  │  └──────────┼─────────────────┼─────────────────────┼─────────────┘  │ │
│  │             │                 │                      │                │ │
│  │  ┌──────────┴─────────────────┴──────────────────────┴─────────────┐  │ │
│  │  │                  TRANSPORT / REMOTE EXECUTION LAYER              │  │ │
│  │  │                                                                  │  │ │
│  │  │  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌────────────────┐  │  │ │
│  │  │  │   SSH    │  │  WinRM   │  │ PowerShell│  │  Agent Gateway │  │  │ │
│  │  │  │ Provider │  │ Provider │  │  Remoting │  │  (gRPC/HTTPS)  │  │  │ │
│  │  │  └──────────┘  └──────────┘  └──────────┘  └────────────────┘  │  │ │
│  │  └──────────────────────────────────────────────────────────────────┘  │ │
│  │                                                                       │ │
│  │  ┌──────────────────────┐  ┌──────────────────────────────────────┐   │ │
│  │  │  Credential Vault    │  │  Logging / Audit Trail Engine        │   │ │
│  │  │  (Encrypted Storage) │  │  (Structured Logs + Event Store)     │   │ │
│  │  └──────────────────────┘  └──────────────────────────────────────┘   │ │
│  │                                                                       │ │
│  │  ┌────────────────────────────────────────────────────────────────┐   │ │
│  │  │                    LOCAL DATA STORE (SQLite)                    │   │ │
│  │  │  Inventory │ Patch History │ Job Log │ Audit Events │ Config   │   │ │
│  │  └────────────────────────────────────────────────────────────────┘   │ │
│  └───────────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────┘

                              │ Network │
          ┌───────────────────┼─────────┼────────────────────┐
          │                   │         │                    │
          ▼                   ▼         ▼                    ▼
   ┌─────────────┐   ┌─────────────┐  ┌─────────────┐ ┌──────────────┐
   │ Linux Host  │   │ Windows Host│  │ Linux Host  │ │ Windows Host │
   │ (SSH)       │   │ (WinRM)     │  │ (Agent)     │ │ (Agent)      │
   │             │   │             │  │             │ │              │
   │ No agent    │   │ No agent    │  │ ┌─────────┐ │ │ ┌──────────┐ │
   │ required    │   │ required    │  │ │  HM     │ │ │ │  HM      │ │
   │             │   │             │  │ │  Agent  │ │ │ │  Agent   │ │
   │             │   │             │  │ └─────────┘ │ │ └──────────┘ │
   └─────────────┘   └─────────────┘  └─────────────┘ └──────────────┘
```

---

## 3. Core Architectural Principles

| Principle | Description |
|---|---|
| **Cross-Platform** | First-class support for Windows and Linux, both as control machine and as managed targets. |
| **Agentless-First** | Default mode uses SSH / WinRM / PS Remoting. Agent is opt-in for restricted networks. |
| **Offline-Capable** | All state stored locally in SQLite. No cloud dependency required. |
| **Modular** | Every capability is a module behind a defined interface; modules can be replaced or extended. |
| **Secure by Default** | Credentials encrypted at rest, least-privilege execution, full audit trail. |
| **Idempotent Operations** | Every patch/service operation is safe to retry without side effects. |

---

## 4. Technology Stack Summary

| Layer | Technology | Rationale |
|---|---|---|
| Language | **C# / .NET 8+** | Cross-platform, rich OS APIs, strong typing, single binary deployment |
| GUI | **Avalonia UI** | Cross-platform desktop UI (Windows, Linux, macOS) with MVVM |
| Data Store | **SQLite** (via EF Core) | Zero-config, embedded, reliable, encrypted via SQLCipher |
| Remote Execution | **SSH.NET**, **WSMan/WinRM** client, **PowerShell SDK** | Native protocol support |
| Agent Communication | **gRPC** over mTLS | Efficient, cross-platform, strongly typed |
| Credential Vault | **DPAPI** (Win) / **libsecret** (Linux) + AES-256-GCM fallback | OS-native where possible |
| Logging | **Serilog** → structured JSON + SQLite sink | Queryable, portable |
| Scheduling | **Quartz.NET** (embedded) | Mature, cron-expression based |
| Testing | **xUnit**, **NSubstitute**, **Testcontainers** | Industry standard |

---

## 5. Module Boundaries at a Glance

```
┌─────────────────────────────────────────────────────────┐
│                    Public API Surface                     │
│                                                          │
│  IInventoryService      IPatchService                    │
│  IServiceController     ICredentialVault                 │
│  IRemoteExecutor        IAuditLogger                     │
│  IJobScheduler          IAgentGateway                    │
│  ICommandBroker                                          │
│                                                          │
└─────────────────────────────────────────────────────────┘
         ▲                    ▲                ▲
         │                    │                │
    GUI/CLI Layer       Core Engine      Agent Process
```

Each interface is defined in a shared `HomeManagement.Abstractions` assembly. Implementations live in separate assemblies loaded via dependency injection.

---

## 6. Deployment Topology

### Minimal (Single Machine)
- Control app runs on the operator's workstation
- Connects to targets via SSH / WinRM directly

### Standard (Home Lab)
- Control app on a dedicated management machine
- Mix of agentless and agent-based targets
- SQLite database on local disk (backed up)

### Advanced (Future)
- Control app backed by a PostgreSQL database
- REST API exposed for automation / CI/CD integration
- Agents report to a central hub
- Optional web dashboard alongside desktop app
