# 10 — Data Architecture

> **Version:** 1.0  
> **Date:** 2026-03-14  
> **Status:** Approved  
> **Audience:** Developers implementing repositories, data access, and storage concerns

This document specifies the complete data architecture of the HomeManagement platform: the SQLite relational schema, entity-relationship model, data flows between modules, indexing strategy, encryption strategy for sensitive fields, and backup/recovery design.

---

## Table of Contents

1. [Data Storage Overview](#1-data-storage-overview)
2. [SQLite Schema](#2-sqlite-schema)
3. [Entity-Relationship Diagram](#3-entity-relationship-diagram)
4. [Data Flow Between Modules](#4-data-flow-between-modules)
5. [Indexing Strategy](#5-indexing-strategy)
6. [Encryption Strategy for Sensitive Fields](#6-encryption-strategy-for-sensitive-fields)
7. [Backup & Recovery](#7-backup--recovery)
8. [Data Lifecycle & Retention](#8-data-lifecycle--retention)
9. [Migration Strategy](#9-migration-strategy)
10. [Performance Characteristics](#10-performance-characteristics)

---

## 1. Data Storage Overview

HomeManagement uses a **two-store architecture**: a relational SQLite database for structured operational data, and a separate encrypted vault file for credential secrets.

```
~/.homemanagement/                    (Linux)
%APPDATA%\HomeManagement\             (Windows)
│
├── homemanagement.db                 ◄── SQLite database (operational data)
│   ├── Machines                          Machine registry
│   ├── MachineTags                       Normalized key-value tags
│   ├── PatchHistory                      Patch lifecycle tracking
│   ├── AuditEvents                       Tamper-evident audit log
│   ├── ServiceSnapshots                  Point-in-time service state captures
│   ├── Jobs                              Job execution tracking
│   ├── JobMachineResults                 Per-machine job outcomes
│   ├── ScheduledJobs                     Cron-based recurring jobs
│   └── AppSettings                       Application configuration store
│
├── vault.enc                         ◄── Encrypted credential vault (AES-256-GCM)
│   └── VaultEntry[]                      Per-entry encrypted credentials
│
├── known_hosts                       ◄── SSH host key fingerprint store
├── certs/                            ◄── mTLS certificate store
│   ├── ca.pfx
│   ├── ca.crt
│   └── server.pfx
└── logs/                             ◄── Rolling log files (Serilog)
    └── hm-YYYYMMDD.log
```

### 1.1 Why Two Stores

| Concern | SQLite Database | Vault File |
|---|---|---|
| **Purpose** | Structured operational data (machines, jobs, audit) | Credential secrets (passwords, SSH keys) |
| **Encryption** | Not encrypted at file level (per-field where needed) | Entire file encrypted (AES-256-GCM) |
| **Access pattern** | Constant reads/writes via EF Core | Opened on unlock, held in memory, flushed on change |
| **Querying** | Rich SQL queries, indexes, pagination | Key-value lookup by credential ID |
| **Concurrency** | WAL mode (multiple readers, single writer) | Single writer (SemaphoreSlim) |
| **Backup** | Standard SQLite `.backup` API | Direct file copy while locked |

### 1.2 Technology Stack

| Component | Technology | Version |
|---|---|---|
| ORM | Entity Framework Core | 8.0.11 |
| Database | SQLite (via `Microsoft.Data.Sqlite`) | 3.x (bundled) |
| Migrations | EF Core Code-First Migrations | — |
| Connection pooling | EF Core built-in | — |
| JSON columns | `System.Text.Json` serialization for denormalized arrays | — |
| WAL mode | Enabled via `PRAGMA journal_mode=WAL` | — |

---

## 2. SQLite Schema

### 2.1 Complete DDL (EF Core generates this; shown here for reference)

```sql
-- ════════════════════════════════════════════════════════════
--  TABLE: Machines
--  Purpose: Central registry of all managed machines
-- ════════════════════════════════════════════════════════════
CREATE TABLE Machines (
    Id                TEXT NOT NULL PRIMARY KEY,     -- GUID as TEXT
    Hostname          TEXT NOT NULL,
    Fqdn              TEXT,
    IpAddresses       TEXT NOT NULL DEFAULT '[]',    -- JSON array of IP strings
    OsType            TEXT NOT NULL,                 -- 'Windows' | 'Linux'
    OsVersion         TEXT NOT NULL DEFAULT '',
    ConnectionMode    TEXT NOT NULL,                 -- 'Agentless' | 'Agent'
    Protocol          TEXT NOT NULL,                 -- 'Ssh' | 'WinRM' | 'PSRemoting' | 'Agent'
    Port              INTEGER NOT NULL,
    CredentialId      TEXT NOT NULL,                 -- FK → vault entry (not DB FK)
    State             TEXT NOT NULL,                 -- 'Online' | 'Offline' | 'Unreachable' | 'Maintenance'
    CpuCores          INTEGER,
    RamBytes          INTEGER,
    Architecture      TEXT,
    DisksJson         TEXT,                          -- JSON array of {MountPoint, TotalBytes, FreeBytes}
    AddedUtc          TEXT NOT NULL,                 -- ISO 8601 UTC
    UpdatedUtc        TEXT NOT NULL,
    LastContactUtc    TEXT NOT NULL,
    IsDeleted         INTEGER NOT NULL DEFAULT 0     -- Soft-delete flag (0/1)
);

CREATE UNIQUE INDEX IX_Machines_Hostname ON Machines (Hostname);


-- ════════════════════════════════════════════════════════════
--  TABLE: MachineTags
--  Purpose: Normalized key-value tags for machine grouping
-- ════════════════════════════════════════════════════════════
CREATE TABLE MachineTags (
    Id                TEXT NOT NULL PRIMARY KEY,
    MachineId         TEXT NOT NULL,
    Key               TEXT NOT NULL,
    Value             TEXT NOT NULL DEFAULT '',
    FOREIGN KEY (MachineId) REFERENCES Machines(Id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IX_MachineTags_MachineId_Key ON MachineTags (MachineId, Key);
CREATE INDEX IX_MachineTags_Key ON MachineTags (Key);
CREATE INDEX IX_MachineTags_Key_Value ON MachineTags (Key, Value);


-- ════════════════════════════════════════════════════════════
--  TABLE: PatchHistory
--  Purpose: Track every patch detection, approval, and install
-- ════════════════════════════════════════════════════════════
CREATE TABLE PatchHistory (
    Id                TEXT NOT NULL PRIMARY KEY,
    MachineId         TEXT NOT NULL,
    PatchId           TEXT NOT NULL,                 -- OS-specific ID (KB number, package name)
    Title             TEXT NOT NULL DEFAULT '',
    Severity          TEXT,                          -- 'Critical' | 'Important' | 'Moderate' | 'Low' | 'Unclassified'
    Category          TEXT,                          -- 'Security' | 'BugFix' | 'Feature' | 'Driver' | 'Other'
    SizeBytes         INTEGER,
    RequiresReboot    INTEGER,                       -- 0 | 1
    State             TEXT NOT NULL,                 -- PatchInstallState as string
    TimestampUtc      TEXT NOT NULL,
    ErrorMessage      TEXT,
    JobId             TEXT,                          -- FK → Jobs.Id (which job applied this patch)
    FOREIGN KEY (MachineId) REFERENCES Machines(Id) ON DELETE CASCADE
);

CREATE INDEX IX_PatchHistory_MachineId ON PatchHistory (MachineId);
CREATE INDEX IX_PatchHistory_TimestampUtc ON PatchHistory (TimestampUtc);
CREATE INDEX IX_PatchHistory_State ON PatchHistory (State);
CREATE INDEX IX_PatchHistory_MachineId_TimestampUtc ON PatchHistory (MachineId, TimestampUtc);
CREATE INDEX IX_PatchHistory_MachineId_PatchId ON PatchHistory (MachineId, PatchId);


-- ════════════════════════════════════════════════════════════
--  TABLE: ServiceSnapshots
--  Purpose: Point-in-time captures of service states
-- ════════════════════════════════════════════════════════════
CREATE TABLE ServiceSnapshots (
    Id                TEXT NOT NULL PRIMARY KEY,
    MachineId         TEXT NOT NULL,
    ServiceName       TEXT NOT NULL,
    DisplayName       TEXT NOT NULL DEFAULT '',
    State             TEXT NOT NULL,                 -- ServiceState as string
    StartupType       TEXT NOT NULL,                 -- ServiceStartupType as string
    ProcessId         INTEGER,
    CapturedUtc       TEXT NOT NULL,
    FOREIGN KEY (MachineId) REFERENCES Machines(Id) ON DELETE CASCADE
);

CREATE INDEX IX_ServiceSnapshots_MachineId ON ServiceSnapshots (MachineId);
CREATE INDEX IX_ServiceSnapshots_MachineId_ServiceName ON ServiceSnapshots (MachineId, ServiceName);
CREATE INDEX IX_ServiceSnapshots_CapturedUtc ON ServiceSnapshots (CapturedUtc);


-- ════════════════════════════════════════════════════════════
--  TABLE: AuditEvents
--  Purpose: Immutable, HMAC-chained audit trail
-- ════════════════════════════════════════════════════════════
CREATE TABLE AuditEvents (
    EventId           TEXT NOT NULL PRIMARY KEY,
    TimestampUtc      TEXT NOT NULL,
    CorrelationId     TEXT NOT NULL,
    Action            TEXT NOT NULL,                 -- AuditAction enum as string
    ActorIdentity     TEXT NOT NULL,
    TargetMachineId   TEXT,
    TargetMachineName TEXT,
    Detail            TEXT,                          -- Human-readable, redacted of sensitive data
    Properties        TEXT,                          -- JSON dict, redacted of sensitive data
    Outcome           TEXT NOT NULL,                 -- 'Success' | 'Failure' | 'PartialSuccess'
    ErrorMessage      TEXT,
    PreviousHash      TEXT,                          -- HMAC chain: hash of preceding event
    EventHash         TEXT                           -- HMAC-SHA256(canonical + PreviousHash, chainKey)
);

CREATE INDEX IX_AuditEvents_TimestampUtc ON AuditEvents (TimestampUtc);
CREATE INDEX IX_AuditEvents_CorrelationId ON AuditEvents (CorrelationId);
CREATE INDEX IX_AuditEvents_Action ON AuditEvents (Action);
CREATE INDEX IX_AuditEvents_Action_Outcome ON AuditEvents (Action, Outcome);
CREATE INDEX IX_AuditEvents_TargetMachineId_TimestampUtc ON AuditEvents (TargetMachineId, TimestampUtc);
CREATE INDEX IX_AuditEvents_ActorIdentity ON AuditEvents (ActorIdentity);


-- ════════════════════════════════════════════════════════════
--  TABLE: Jobs
--  Purpose: Track multi-machine job execution
-- ════════════════════════════════════════════════════════════
CREATE TABLE Jobs (
    Id                TEXT NOT NULL PRIMARY KEY,
    Name              TEXT NOT NULL,
    Type              TEXT NOT NULL,                 -- JobType as string
    State             TEXT NOT NULL,                 -- JobState as string
    SubmittedUtc      TEXT NOT NULL,
    StartedUtc        TEXT,
    CompletedUtc      TEXT,
    TotalTargets      INTEGER NOT NULL DEFAULT 0,
    CompletedTargets  INTEGER NOT NULL DEFAULT 0,
    FailedTargets     INTEGER NOT NULL DEFAULT 0,
    DefinitionJson    TEXT,                          -- Serialized JobDefinition for replay
    CorrelationId     TEXT                           -- Links job to audit trail
);

CREATE INDEX IX_Jobs_SubmittedUtc ON Jobs (SubmittedUtc);
CREATE INDEX IX_Jobs_Type_State ON Jobs (Type, State);
CREATE INDEX IX_Jobs_State ON Jobs (State);


-- ════════════════════════════════════════════════════════════
--  TABLE: JobMachineResults
--  Purpose: Per-machine outcome for each job
-- ════════════════════════════════════════════════════════════
CREATE TABLE JobMachineResults (
    Id                TEXT NOT NULL PRIMARY KEY,
    JobId             TEXT NOT NULL,
    MachineId         TEXT NOT NULL,
    MachineName       TEXT NOT NULL DEFAULT '',
    Success           INTEGER NOT NULL DEFAULT 0,    -- 0 | 1
    ErrorMessage      TEXT,
    DurationMs        INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
);

CREATE INDEX IX_JobMachineResults_JobId ON JobMachineResults (JobId);
CREATE INDEX IX_JobMachineResults_MachineId ON JobMachineResults (MachineId);
CREATE INDEX IX_JobMachineResults_MachineId_Success ON JobMachineResults (MachineId, Success);


-- ════════════════════════════════════════════════════════════
--  TABLE: ScheduledJobs
--  Purpose: Cron-based recurring job definitions
-- ════════════════════════════════════════════════════════════
CREATE TABLE ScheduledJobs (
    Id                TEXT NOT NULL PRIMARY KEY,
    Name              TEXT NOT NULL,
    Type              TEXT NOT NULL,                 -- JobType as string
    CronExpression    TEXT NOT NULL,
    DefinitionJson    TEXT,                          -- Serialized JobDefinition
    IsEnabled         INTEGER NOT NULL DEFAULT 1,    -- 0 | 1
    NextFireUtc       TEXT,
    LastFireUtc       TEXT,
    CreatedUtc        TEXT NOT NULL,
    UpdatedUtc        TEXT NOT NULL
);

CREATE INDEX IX_ScheduledJobs_IsEnabled ON ScheduledJobs (IsEnabled);


-- ════════════════════════════════════════════════════════════
--  TABLE: AppSettings
--  Purpose: Key-value application configuration
-- ════════════════════════════════════════════════════════════
CREATE TABLE AppSettings (
    Key               TEXT NOT NULL PRIMARY KEY,
    Value             TEXT NOT NULL,
    UpdatedUtc        TEXT NOT NULL
);
```

### 2.2 Column Type Conventions

| .NET Type | SQLite Column | Conversion |
|---|---|---|
| `Guid` | `TEXT` | `Guid.ToString()` / `Guid.Parse()` |
| `DateTime` | `TEXT` | ISO 8601 UTC (e.g., `2026-03-14T10:30:00.000Z`) |
| `TimeSpan` | `INTEGER` | Stored as milliseconds (DurationMs) |
| `bool` | `INTEGER` | 0 = false, 1 = true |
| `enum` | `TEXT` | Enum member name as string (e.g., `"Windows"`, `"Running"`) |
| `IPAddress[]` | `TEXT` | JSON array: `["192.168.1.10","10.0.0.5"]` |
| `DiskInfo[]` | `TEXT` | JSON array of objects |
| `Dictionary<string,string>` | `TEXT` | JSON object: `{"key":"value"}` |
| `int`, `long` | `INTEGER` | Native SQLite integer |

### 2.3 Enum Storage

All enums are stored as **strings** (not integers) for:
- Human readability when inspecting the database
- Forward compatibility (new enum values won't break old data)
- Meaningful index values in WHERE clauses

EF Core conversion is configured per-entity in `OnModelCreating`:
```csharp
e.Property(m => m.OsType).HasConversion<string>();
```

---

## 3. Entity-Relationship Diagram

### 3.1 Full ER Diagram

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                      HOMEMANAGEMENT DATA MODEL                               │
└──────────────────────────────────────────────────────────────────────────────┘

  ┌─────────────────────────┐         ┌──────────────────────────┐
  │  vault.enc (File Store)  │         │     AppSettings          │
  │─────────────────────────│         │──────────────────────────│
  │ VaultEntry[]             │         │ PK  Key         TEXT     │
  │ ├─ Id          GUID     │         │     Value       TEXT     │
  │ ├─ Label       string   │         │     UpdatedUtc  TEXT     │
  │ ├─ Type        enum     │         └──────────────────────────┘
  │ ├─ Username    string   │
  │ ├─ EncPayload  byte[]   │◄──────────── CredentialId (logical FK)
  │ ├─ EntryNonce  byte[]   │               │
  │ ├─ EntryTag    byte[]   │               │
  │ └─ MachineIds  GUID[]   │               │
  └─────────────────────────┘               │
                                            │
  ┌─────────────────────────────────────────┴───────────────────────────────┐
  │                            Machines                                      │
  │─────────────────────────────────────────────────────────────────────────│
  │ PK  Id                  TEXT (GUID)                                      │
  │ UQ  Hostname            TEXT            ◄── Validated: Hostname type     │
  │     Fqdn                TEXT?                                            │
  │     IpAddresses         TEXT (JSON[])                                    │
  │     OsType              TEXT (enum)     ◄── 'Windows' | 'Linux'         │
  │     OsVersion           TEXT                                             │
  │     ConnectionMode      TEXT (enum)     ◄── 'Agentless' | 'Agent'       │
  │     Protocol            TEXT (enum)     ◄── 'Ssh'|'WinRM'|'PSRemoting'  │
  │     Port                INTEGER                                          │
  │     CredentialId        TEXT (GUID)     ──► vault.enc (logical FK)      │
  │     State               TEXT (enum)     ◄── 'Online'|'Offline'|...      │
  │     CpuCores            INTEGER?                                         │
  │     RamBytes            INTEGER?                                         │
  │     Architecture        TEXT?                                            │
  │     DisksJson           TEXT? (JSON[])                                   │
  │     AddedUtc            TEXT                                             │
  │     UpdatedUtc          TEXT                                             │
  │     LastContactUtc      TEXT                                             │
  │     IsDeleted           INTEGER (bool)  ◄── Soft-delete (query filter)  │
  │                                                                          │
  │  QF: WHERE IsDeleted = 0  (global query filter)                         │
  └────────┬────────────────────┬────────────────────┬──────────────────────┘
           │ 1:N                │ 1:N                │ 1:N
           │ CASCADE            │ CASCADE             │ CASCADE
           ▼                    ▼                     ▼
  ┌──────────────────┐ ┌────────────────────┐ ┌─────────────────────────┐
  │   MachineTags    │ │   PatchHistory     │ │   ServiceSnapshots      │
  │──────────────────│ │────────────────────│ │─────────────────────────│
  │ PK Id      TEXT  │ │ PK Id       TEXT   │ │ PK Id           TEXT    │
  │ FK MachineId     │ │ FK MachineId       │ │ FK MachineId            │
  │    Key     TEXT  │ │    PatchId  TEXT   │ │    ServiceName  TEXT    │
  │    Value   TEXT  │ │    Title    TEXT   │ │    DisplayName  TEXT    │
  │                  │ │    Severity TEXT   │ │    State        TEXT    │
  │ UQ(MachineId,Key)│ │    Category TEXT   │ │    StartupType  TEXT    │
  └──────────────────┘ │    SizeBytes INT  │ │    ProcessId    INT?    │
                       │    ReqReboot INT  │ │    CapturedUtc  TEXT    │
                       │    State    TEXT   │ └─────────────────────────┘
                       │    TimestampUtc    │
                       │    ErrorMessage    │
                       │ FK JobId    TEXT?  │──┐
                       └────────────────────┘  │
                                               │
  ┌────────────────────────────────────────────┘
  │
  │  ┌──────────────────────────────────────────────────────────────────┐
  │  │                              Jobs                                │
  │  │──────────────────────────────────────────────────────────────────│
  │  │ PK  Id               TEXT (GUID)                                 │
  │  │     Name              TEXT                                       │
  │  │     Type              TEXT (enum)    ◄── 'PatchScan'|'PatchApply'│
  │  │     State             TEXT (enum)    ◄── 'Queued'|'Running'|...  │
  │  │     SubmittedUtc      TEXT                                       │
  │  │     StartedUtc        TEXT?                                      │
  │  │     CompletedUtc      TEXT?                                      │
  │  │     TotalTargets      INTEGER                                    │
  │  │     CompletedTargets  INTEGER                                    │
  │  │     FailedTargets     INTEGER                                    │
  │  │     DefinitionJson    TEXT? (JSON)   ◄── Full JobDefinition      │
  └──►     CorrelationId     TEXT?          ──► AuditEvents link        │
     │                                                                  │
     │  1:N  CASCADE                                                     │
     └──────────────────────────┬───────────────────────────────────────┘
                                │
                                ▼
     ┌──────────────────────────────────────────┐
     │          JobMachineResults                │
     │──────────────────────────────────────────│
     │ PK  Id               TEXT (GUID)          │
     │ FK  JobId             TEXT                 │──► Jobs.Id
     │     MachineId         TEXT                 │──► Machines.Id (logical)
     │     MachineName       TEXT                 │
     │     Success           INTEGER (bool)       │
     │     ErrorMessage      TEXT?                 │
     │     DurationMs        INTEGER               │
     └──────────────────────────────────────────┘


  ┌──────────────────────────────────────────────────────────────────────┐
  │                          AuditEvents                                 │
  │──────────────────────────────────────────────────────────────────────│
  │ PK  EventId          TEXT (GUID)                                     │
  │     TimestampUtc      TEXT                                           │
  │     CorrelationId     TEXT           ──► Traces back to Jobs, UI ops │
  │     Action            TEXT (enum)    ◄── 27 distinct audit actions   │
  │     ActorIdentity     TEXT                                           │
  │     TargetMachineId   TEXT?          ──► Machines.Id (logical FK)    │
  │     TargetMachineName TEXT?                                          │
  │     Detail            TEXT?          ◄── Redacted by ISensitiveData  │
  │     Properties        TEXT? (JSON)   ◄── Redacted by ISensitiveData  │
  │     Outcome           TEXT (enum)    ◄── 'Success'|'Failure'|...     │
  │     ErrorMessage      TEXT?                                          │
  │     PreviousHash      TEXT?          ──► Previous AuditEvent hash    │
  │     EventHash         TEXT?          ◄── HMAC-SHA256 chain link      │
  │                                                                      │
  │  CONSTRAINTS: Append-only (no UPDATE, no DELETE exposed via repo)    │
  └──────────────────────────────────────────────────────────────────────┘


  ┌──────────────────────────────────────────────────────────────────────┐
  │                        ScheduledJobs                                 │
  │──────────────────────────────────────────────────────────────────────│
  │ PK  Id               TEXT (GUID)                                     │
  │     Name              TEXT                                           │
  │     Type              TEXT (enum)                                     │
  │     CronExpression    TEXT           ◄── Quartz cron syntax          │
  │     DefinitionJson    TEXT? (JSON)   ◄── Serialized JobDefinition    │
  │     IsEnabled         INTEGER (bool)                                  │
  │     NextFireUtc       TEXT?                                          │
  │     LastFireUtc       TEXT?                                          │
  │     CreatedUtc        TEXT                                           │
  │     UpdatedUtc        TEXT                                           │
  └──────────────────────────────────────────────────────────────────────┘
```

### 3.2 Relationship Summary

| Parent | Child | Cardinality | On Delete | FK Column |
|---|---|---|---|---|
| `Machines` | `MachineTags` | 1:N | CASCADE | `MachineId` |
| `Machines` | `PatchHistory` | 1:N | CASCADE | `MachineId` |
| `Machines` | `ServiceSnapshots` | 1:N | CASCADE | `MachineId` |
| `Jobs` | `JobMachineResults` | 1:N | CASCADE | `JobId` |
| `Machines` → `vault.enc` | — | N:1 (logical) | — | `CredentialId` |
| `PatchHistory` → `Jobs` | — | N:1 (logical) | — | `JobId` |
| `JobMachineResults` → `Machines` | — | N:1 (logical) | — | `MachineId` |
| `AuditEvents` → `Machines` | — | N:1 (logical) | — | `TargetMachineId` |
| `AuditEvents` → `AuditEvents` | — | 1:1 (chain) | — | `PreviousHash` |

**Logical FKs** are intentional — they enable querying without creating rigid coupling. `CredentialId` references vault entries that exist outside the database. `TargetMachineId` on audit events preserves history even if a machine is hard-deleted.

### 3.3 Table Cardinality Estimates (100-machine deployment)

| Table | Rows/month | Rows/year | Growth Rate |
|---|---|---|---|
| `Machines` | ~100 | ~100 | Static (soft-delete, not growing) |
| `MachineTags` | ~300 | ~300 | Static (~3 tags per machine average) |
| `PatchHistory` | ~3,000 | ~36,000 | ~30 patches/machine/month |
| `ServiceSnapshots` | ~60,000 | ~720,000 | ~20 services × 100 machines × monthly snapshots |
| `AuditEvents` | ~10,000 | ~120,000 | ~100 audit events/day |
| `Jobs` | ~300 | ~3,600 | ~10 jobs/day |
| `JobMachineResults` | ~3,000 | ~36,000 | ~10 results/job × 10 jobs/day |
| `ScheduledJobs` | ~10 | ~10 | Static (user-defined schedules) |
| `AppSettings` | ~20 | ~20 | Static |

**Total projected yearly: ~915,000 rows.** Well within SQLite's comfort zone (handles millions without issue).

---

## 4. Data Flow Between Modules

### 4.1 Module-to-Storage Mapping

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        MODULE DATA ACCESS MAP                            │
│                                                                          │
│  Module                    │ Repository Interface      │ Tables Accessed  │
│  ──────────────────────────┼──────────────────────────┼─────────────────│
│  Inventory                 │ IMachineRepository        │ Machines,        │
│                            │                           │ MachineTags      │
│  ──────────────────────────┼──────────────────────────┼─────────────────│
│  Patching                  │ IPatchHistoryRepository   │ PatchHistory     │
│  ──────────────────────────┼──────────────────────────┼─────────────────│
│  Services                  │ (new) IServiceSnapshot-   │ ServiceSnapshots │
│                            │        Repository         │                  │
│  ──────────────────────────┼──────────────────────────┼─────────────────│
│  Auditing                  │ IAuditEventRepository     │ AuditEvents      │
│  ──────────────────────────┼──────────────────────────┼─────────────────│
│  Orchestration             │ IJobRepository            │ Jobs,            │
│                            │                           │ JobMachineResults│
│                            │                           │ ScheduledJobs    │
│  ──────────────────────────┼──────────────────────────┼─────────────────│
│  Vault                     │ (file I/O: VaultFileStore)│ vault.enc        │
│  ──────────────────────────┼──────────────────────────┼─────────────────│
│  Core                      │ (direct EF / AppSettings) │ AppSettings      │
│  ──────────────────────────┼──────────────────────────┼─────────────────│
│  Transport                 │ (none — stateless)        │ —                │
│  ──────────────────────────┼──────────────────────────┼─────────────────│
│  GUI                       │ (via service interfaces)  │ (indirect)       │
└──────────────────────────────────────────────────────────────────────────┘
```

### 4.2 Data Flow: Patch Detection → Apply → History

```
 User clicks "Scan for Patches"
           │
           ▼
 ┌──── GUI Layer ──────────────────────────────────────────────────────┐
 │  PatchScanViewModel                                                  │
 │  └─► IJobScheduler.SubmitAsync(JobDefinition{PatchScan, machineIds}) │
 └──────────────────────────────────────┬───────────────────────────────┘
                                        │
                                        ▼  WRITE
                                  ┌──────────┐
                                  │   Jobs    │ State='Queued'
                                  └─────┬────┘
                                        │
 ┌──── Orchestration Layer ─────────────┼───────────────────────────────┐
 │  JobExecutor picks up queued job     │                               │
 │  └─► ForEach machineId:             ▼  UPDATE                       │
 │       │                        ┌──────────┐                          │
 │       │                        │   Jobs    │ State='Running'         │
 │       ▼                        └──────────┘                          │
 │  IInventoryService.GetAsync(machineId)                               │
 │       │                                                              │
 │       ▼  READ                                                        │
 │  ┌──────────┐                                                        │
 │  │ Machines │ → MachineTarget                                        │
 │  └──────────┘                                                        │
 │       │                                                              │
 │       ▼                                                              │
 │  ICredentialVault.GetPayloadAsync(credentialId)                      │
 │       │                                                              │
 │       ▼  READ                                                        │
 │  ┌────────────┐                                                      │
 │  │ vault.enc  │ → CredentialPayload (IDisposable)                    │
 │  └────────────┘                                                      │
 │       │                                                              │
 │       ▼                                                              │
 │  IPatchService.DetectAsync(target)                                   │
 │       │                                                              │
 │       ▼                                                              │
 │  IRemoteExecutor.ExecuteAsync(target, detectCommand)                 │
 │       │  (SSH/WinRM/Agent — no DB access)                            │
 │       │                                                              │
 │       ▼  Returns PatchInfo[]                                         │
 │                                                                      │
 │  ── If user approves and submits PatchApply job: ──                  │
 │                                                                      │
 │  IPatchService.ApplyAsync(target, patches, options)                  │
 │       │                                                              │
 │       ▼                                                              │
 │  IRemoteExecutor.ExecuteAsync(target, installCommand)                │
 │       │                                                              │
 │       ▼  Returns PatchResult (per-patch outcomes)                    │
 │                                                                      │
 │  ForEach PatchOutcome:                                               │
 │       │                                                              │
 │       ▼  WRITE                                                       │
 │  ┌──────────────┐                                                    │
 │  │ PatchHistory  │ One row per patch per machine                     │
 │  └──────────────┘                                                    │
 │       │                                                              │
 │       ▼  WRITE                                                       │
 │  ┌────────────────────┐                                              │
 │  │ JobMachineResults  │ Success/fail per machine                     │
 │  └────────────────────┘                                              │
 │       │                                                              │
 │       ▼  WRITE                                                       │
 │  ┌──────────────┐                                                    │
 │  │ AuditEvents  │ PatchInstallStarted → PatchInstallCompleted        │
 │  └──────────────┘                                                    │
 │       │                                                              │
 │       ▼  UPDATE                                                      │
 │  ┌──────────┐                                                        │
 │  │   Jobs   │ State='Completed', CompletedTargets++                  │
 │  └──────────┘                                                        │
 └──────────────────────────────────────────────────────────────────────┘
```

### 4.3 Data Flow: Service Control

```
 User clicks "Restart nginx on server-01"
           │
           ▼
 ┌──── GUI Layer ─────────────────────────────────────────────┐
 │  ServiceManagerViewModel                                     │
 │  └─► IServiceController.ControlAsync(target, "nginx", Restart)│
 └───────────────────────────────────┬─────────────────────────┘
                                     │
 ┌──── Services Layer ───────────────┼─────────────────────────┐
 │                                   │                          │
 │  1. Resolve credential:          ▼  READ                    │
 │     ICredentialVault.GetPayloadAsync()                       │
 │                                   │                          │
 │  2. Execute remote command:       ▼                          │
 │     IRemoteExecutor.ExecuteAsync(target,                     │
 │       "systemctl restart nginx")                              │
 │                                   │                          │
 │  3. Parse result:                 ▼  Returns                │
 │     ServiceActionResult{Success, ResultingState, Duration}   │
 │                                   │                          │
 │  4. Capture snapshot:            ▼  WRITE                   │
 │     ┌───────────────────┐                                    │
 │     │ ServiceSnapshots  │ Post-action state capture          │
 │     └───────────────────┘                                    │
 │                                   │                          │
 │  5. Audit:                       ▼  WRITE                   │
 │     ┌──────────────┐                                         │
 │     │ AuditEvents  │ ServiceRestarted                        │
 │     └──────────────┘                                         │
 └──────────────────────────────────────────────────────────────┘
```

### 4.4 Data Flow: Machine Discovery + Registration

```
 User enters "192.168.1.0/24" and clicks "Discover"
           │
           ▼
 ┌──── Inventory Layer ──────────────────────────────────────────┐
 │                                                                │
 │  1. NetworkScanner.ScanRangeAsync(cidr)                        │
 │     └─► ICMP + TCP port probe (no DB access)                  │
 │                                                                │
 │  2. MachineProber.ProbeAsync(ip)                               │
 │     └─► IRemoteExecutor.TestConnectionAsync() (no DB access)   │
 │                                                                │
 │  3. Return candidates to GUI (not persisted yet)               │
 │                                                                │
 │  User selects discovered machines and clicks "Add"             │
 │           │                                                    │
 │           ▼                                                    │
 │  4. IInventoryService.AddAsync(MachineCreateRequest)           │
 │           │                                                    │
 │           ▼  WRITE                                             │
 │     ┌──────────┐                                               │
 │     │ Machines │ New row (IsDeleted=0)                         │
 │     └──────────┘                                               │
 │           │                                                    │
 │           ▼  WRITE                                             │
 │     ┌──────────────┐                                           │
 │     │ MachineTags  │ N rows (one per tag key-value pair)       │
 │     └──────────────┘                                           │
 │           │                                                    │
 │           ▼  WRITE                                             │
 │     ┌──────────────┐                                           │
 │     │ AuditEvents  │ MachineAdded                              │
 │     └──────────────┘                                           │
 └────────────────────────────────────────────────────────────────┘
```

### 4.5 Cross-Module Data Dependencies

```
                    ┌───────────┐
                    │  vault.enc │
                    └─────┬─────┘
                          │ CredentialId
          ┌───────────────┼───────────────┐
          ▼               ▼               ▼
    ┌──────────┐    ┌──────────┐    ┌──────────┐
    │ Machines │    │Transport │    │  Agent   │
    │ (store)  │    │(runtime) │    │(runtime) │
    └────┬─────┘    └──────────┘    └──────────┘
         │
    ┌────┼──────────────────┬──────────────────┐
    ▼    ▼                  ▼                  ▼
┌────────┐  ┌──────────────┐ ┌────────────────┐ ┌──────────────┐
│Machine │  │ PatchHistory │ │ServiceSnapshots│ │ AuditEvents  │
│Tags    │  └──────┬───────┘ └────────────────┘ └──────┬───────┘
└────────┘         │                                    │
                   │ JobId                              │ CorrelationId
                   ▼                                    ▼
             ┌──────────┐                        ┌──────────┐
             │   Jobs   │────CorrelationId──────►│ AuditEvents│
             └────┬─────┘                        └──────────┘
                  │ 1:N
                  ▼
          ┌──────────────────┐
          │ JobMachineResults│
          └──────────────────┘
```

---

## 5. Indexing Strategy

### 5.1 Index Catalog

Every index is justified by a specific query pattern from the application layer.

#### Machines

| Index | Columns | Type | Query Pattern |
|---|---|---|---|
| `PK` | `Id` | Primary Key | All lookups by ID |
| `IX_Machines_Hostname` | `Hostname` | Unique | Duplicate detection, search by name |

**Global query filter** `WHERE IsDeleted = 0` is applied by EF Core to every query. The `IsDeleted` column is intentionally NOT indexed — the filter is applied after other index lookups, and a boolean column has very low selectivity (poor index candidate).

#### MachineTags

| Index | Columns | Type | Query Pattern |
|---|---|---|---|
| `PK` | `Id` | Primary Key | — |
| `IX_MachineTags_MachineId_Key` | `(MachineId, Key)` | Unique | "Get tag X for machine Y", prevent duplicates |
| `IX_MachineTags_Key` | `Key` | Non-unique | "Find all machines tagged 'role'" (any value) |
| `IX_MachineTags_Key_Value` | `(Key, Value)` | Non-unique | "Find machines where role=web" |

#### PatchHistory

| Index | Columns | Type | Query Pattern |
|---|---|---|---|
| `PK` | `Id` | Primary Key | — |
| `IX_PatchHistory_MachineId` | `MachineId` | Non-unique | "All patches for machine X" |
| `IX_PatchHistory_TimestampUtc` | `TimestampUtc` | Non-unique | "Recent patches across all machines" |
| `IX_PatchHistory_State` | `State` | Non-unique | "All patches in 'Installing' state" |
| `IX_PatchHistory_MachineId_TimestampUtc` | `(MachineId, TimestampUtc)` | Non-unique | "Patch history for machine X, sorted by date" |
| `IX_PatchHistory_MachineId_PatchId` | `(MachineId, PatchId)` | Non-unique | "Has KB5001234 been installed on server-01?" |

#### ServiceSnapshots

| Index | Columns | Type | Query Pattern |
|---|---|---|---|
| `PK` | `Id` | Primary Key | — |
| `IX_ServiceSnapshots_MachineId` | `MachineId` | Non-unique | "All service states for machine X" |
| `IX_ServiceSnapshots_MachineId_ServiceName` | `(MachineId, ServiceName)` | Non-unique | "History of nginx state on machine X" |
| `IX_ServiceSnapshots_CapturedUtc` | `CapturedUtc` | Non-unique | "Most recent snapshots across fleet" |

#### AuditEvents

| Index | Columns | Type | Query Pattern |
|---|---|---|---|
| `PK` | `EventId` | Primary Key | Individual event lookup |
| `IX_AuditEvents_TimestampUtc` | `TimestampUtc` | Non-unique | "Events in time range", HMAC chain traversal |
| `IX_AuditEvents_CorrelationId` | `CorrelationId` | Non-unique | "All events from a single operation" |
| `IX_AuditEvents_Action` | `Action` | Non-unique | "All PatchInstall events" |
| `IX_AuditEvents_Action_Outcome` | `(Action, Outcome)` | Non-unique | "All failed patch installs" |
| `IX_AuditEvents_TargetMachineId_TimestampUtc` | `(TargetMachineId, TimestampUtc)` | Non-unique | "Audit trail for machine X" |
| `IX_AuditEvents_ActorIdentity` | `ActorIdentity` | Non-unique | "All actions by user jdoe" |

#### Jobs

| Index | Columns | Type | Query Pattern |
|---|---|---|---|
| `PK` | `Id` | Primary Key | — |
| `IX_Jobs_SubmittedUtc` | `SubmittedUtc` | Non-unique | "Recent jobs" |
| `IX_Jobs_Type_State` | `(Type, State)` | Non-unique | "All running patch jobs" |
| `IX_Jobs_State` | `State` | Non-unique | "All jobs in Queued state" (scheduler pickup) |

#### JobMachineResults

| Index | Columns | Type | Query Pattern |
|---|---|---|---|
| `PK` | `Id` | Primary Key | — |
| `IX_JobMachineResults_JobId` | `JobId` | Non-unique | "All results for job X" |
| `IX_JobMachineResults_MachineId` | `MachineId` | Non-unique | "All job results for machine X" |
| `IX_JobMachineResults_MachineId_Success` | `(MachineId, Success)` | Non-unique | "All failed jobs on machine X" |

#### ScheduledJobs

| Index | Columns | Type | Query Pattern |
|---|---|---|---|
| `PK` | `Id` | Primary Key | — |
| `IX_ScheduledJobs_IsEnabled` | `IsEnabled` | Non-unique | "All active schedules" |

### 5.2 Indexing Principles

1. **No over-indexing** — each index is justified by a concrete query from the ViewModel or repository layer. SQLite indexes cost ~50 bytes per row overhead.
2. **Composite indexes ordered by selectivity** — most selective column first (e.g., `MachineId` before `TimestampUtc`).
3. **Covering indexes not needed** — SQLite's B-tree already stores the rowid; for a <1M row database the random I/O cost of rowid lookups is negligible.
4. **No LIKE-based indexes** — full-text search on `Detail` or `Hostname` uses `LIKE '%term%'` which cannot use B-tree indexes. If needed, add SQLite FTS5 virtual table later.
5. **Enum columns indexed as strings** — since enums are stored as strings, index lookup matches the query predicate exactly. String comparison is fast for short enum values.

### 5.3 SQLite Configuration Pragmas

```sql
-- Set at connection open (EF Core connection interceptor)
PRAGMA journal_mode = WAL;          -- Write-Ahead Logging (concurrent readers)
PRAGMA synchronous = NORMAL;        -- Balanced durability vs. performance
PRAGMA foreign_keys = ON;           -- Enforce FK constraints
PRAGMA busy_timeout = 5000;         -- Wait 5s for lock rather than fail immediately
PRAGMA cache_size = -8000;          -- 8 MB page cache (negative = KB)
PRAGMA temp_store = MEMORY;         -- Temp tables in memory
PRAGMA mmap_size = 134217728;       -- 128 MB memory-mapped I/O
```

---

## 6. Encryption Strategy for Sensitive Fields

### 6.1 Data Sensitivity Classification

```
┌───────────────────────────────────────────────────────────────────────┐
│                    DATA SENSITIVITY TIERS                              │
│                                                                       │
│  TIER 1 — SECRET (never in database)                                 │
│  ┌────────────────────────────────────────────────────────────────┐   │
│  │  Passwords, SSH private keys, Kerberos tickets                 │   │
│  │  HMAC chain key, Vault master key                              │   │
│  │                                                                │   │
│  │  Storage: vault.enc (AES-256-GCM, Argon2id-derived key)       │   │
│  │  In-memory: GCHandle.Pinned + ZeroMemory on dispose            │   │
│  │  NOT in SQLite. Ever.                                          │   │
│  └────────────────────────────────────────────────────────────────┘   │
│                                                                       │
│  TIER 2 — SENSITIVE (in database, protected)                         │
│  ┌────────────────────────────────────────────────────────────────┐   │
│  │  Machine IP addresses   — in Machines.IpAddresses (JSON)       │   │
│  │  Machine hostnames      — in Machines.Hostname                 │   │
│  │  CredentialId           — in Machines.CredentialId             │   │
│  │  Error messages         — may contain stack traces/paths       │   │
│  │  Agent cert serials     — in AuditEvents.Properties            │   │
│  │                                                                │   │
│  │  Protection: File ACLs (OS-level), logical FK to vault         │   │
│  │  CredentialId is a GUID pointer — not the credential itself    │   │
│  └────────────────────────────────────────────────────────────────┘   │
│                                                                       │
│  TIER 3 — OPERATIONAL (in database, public within organization)      │
│  ┌────────────────────────────────────────────────────────────────┐   │
│  │  OS type/version, hardware info, patch names/IDs               │   │
│  │  Service names/states, job definitions, audit actions           │   │
│  │  Tag keys/values, timestamps                                   │   │
│  │                                                                │   │
│  │  Protection: File ACLs only                                    │   │
│  └────────────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────────────┘
```

### 6.2 Credential Storage Path (vault.enc)

Credentials are **never stored in, queried from, or passed through the SQLite database**. The relationship between machines and credentials is a logical FK (`CredentialId` GUID) that points to the vault:

```
Machine Row (SQLite)                    Vault Entry (vault.enc)
┌─────────────────────────────┐        ┌───────────────────────────────┐
│ Id: a1b2c3...               │        │ Id: d4e5f6...                 │
│ Hostname: "server-01"       │   ──►  │ Label: "server-01 SSH key"    │
│ CredentialId: d4e5f6...     │        │ Type: SshKey                  │
│ ...                         │        │ Username: "hm-operator"       │
└─────────────────────────────┘        │ EncryptedPayload: <AES-GCM>  │
                                       │ EntryNonce: <12 bytes>        │
                                       │ EntryTag: <16 bytes>          │
                                       └───────────────────────────────┘
                                       
  SQLite column holds only the GUID.
  The actual secret never touches the database.
```

### 6.3 Audit Event Redaction Pipeline

Before any audit event is persisted, the `ISensitiveDataFilter` redaction pipeline is applied:

```
Raw Audit Event
│ Detail: "Connected to 192.168.1.10 with password=MySecret123"
│ Properties: {"credential_label": "prod-key", "password": "MySecret123"}
│
▼  ISensitiveDataFilter.Redact(detail)
│  ISensitiveDataFilter.RedactProperties(properties)
│
Redacted Audit Event
│ Detail: "Connected to 192.168.1.10 with password=***REDACTED***"
│ Properties: {"credential_label": "prod-key", "password": "***REDACTED***"}
│
▼  Persisted to AuditEvents table
```

### 6.4 SQLite Database File Protection

The database file itself is not encrypted (no SQLCipher/SEE), relying instead on OS-level access controls:

| Platform | Protection | Implementation |
|---|---|---|
| **Linux** | `chmod 600 ~/.homemanagement/homemanagement.db` | Set on first creation; verified on each startup |
| **Windows** | NTFS ACL: current user (Full Control), deny all others | Set on first creation; verified on each startup |

**Rationale**: SQLite file-level encryption (e.g., SQLCipher) adds ~10% performance overhead and complicates EF Core provider configuration. Since:
- No secrets are stored in the database (Tier 1 data is in vault.enc)
- The database is local-only (no network exposure)
- OS file permissions restrict access to the current user
- Audit events are integrity-protected by the HMAC chain

File-level encryption is deferred to a future version. If required:

```
Migration path to encrypted SQLite:
1. Replace Microsoft.EntityFrameworkCore.Sqlite with SQLitePCLRaw.bundle_sqlcipher
2. Add key= parameter to connection string
3. Derive SQLite encryption key from vault master password (separate KDF derivation)
4. Transparent to all EF Core queries — no code changes required above Data layer
```

### 6.5 HMAC Chain Integrity Protection

The `AuditEvents` table is protected against tampering by the HMAC-SHA256 chain:

```
EventHash[n] = HMAC-SHA256(chainKey, canonical(event[n]) + EventHash[n-1])

Where:
  chainKey  = 32-byte random, stored as a credential in vault.enc
  canonical = deterministic JSON serialization (sorted keys, no whitespace, UTC)

Provides:
  ✔ Event integrity — modifying any field changes the hash
  ✔ Ordering integrity — reordering events breaks the chain
  ✔ Deletion detection — removing an event breaks the chain
  ✘ Does NOT prevent — reading events (that's what ACLs are for)
  ✘ Does NOT prevent — complete DB replacement (use external checkpoints)
```

---

## 7. Backup & Recovery

### 7.1 Backup Components

A complete backup of HomeManagement requires these files:

| Component | File | Size | Backup Method | Frequency |
|---|---|---|---|---|
| **SQLite Database** | `homemanagement.db` | 5–50 MB | SQLite `.backup` API | Daily or before major operations |
| **Credential Vault** | `vault.enc` | 1–100 KB | Direct file copy (already encrypted) | On every credential change |
| **SSH Known Hosts** | `known_hosts` | ~5 KB | Direct file copy | On change |
| **mTLS Certificates** | `certs/` directory | ~10 KB | Direct file copy | On change |
| **Application Config** | `appsettings*.json` | ~2 KB | Direct file copy | On change |
| **Application Logs** | `logs/` directory | 1–100 MB | Optional (diagnostic only) | Not required |

### 7.2 SQLite Backup Strategy

```
┌──────────────────────────────────────────────────────────────────────┐
│  SQLITE BACKUP PROCEDURE                                              │
│                                                                       │
│  Method: SQLite Online Backup API (safe during active use)           │
│  ─────────────────────────────────────────────────────────────────── │
│                                                                       │
│  Implementation:                                                      │
│                                                                       │
│    public async Task BackupAsync(string destinationPath)              │
│    {                                                                  │
│        using var source = new SqliteConnection(connectionString);     │
│        using var dest = new SqliteConnection($"Data Source=...");     │
│        await source.OpenAsync();                                      │
│        await dest.OpenAsync();                                        │
│        source.BackupDatabase(dest);  // Atomic, consistent backup    │
│    }                                                                  │
│                                                                       │
│  Properties:                                                         │
│  ├── Consistent: backup is a point-in-time snapshot                  │
│  ├── Non-blocking: readers and writers can continue during backup    │
│  ├── Atomic: incomplete backup = no file (or old file untouched)     │
│  └── WAL-aware: includes all committed WAL pages                     │
│                                                                       │
│  Backup Schedule:                                                    │
│  ├── Automatic: daily at 02:00 local time (configurable)             │
│  ├── Before: major operations (mass patching, key rotation)          │
│  ├── Manual: user-triggered via GUI (Settings → Backup)              │
│  └── Retention: keep last 7 daily backups                            │
│                                                                       │
│  Backup Naming:                                                      │
│  └── homemanagement-backup-20260314-020000.db                        │
│                                                                       │
│  Storage Location:                                                   │
│  ├── Default: ~/.homemanagement/backups/                              │
│  └── Configurable: external drive, network share                     │
└──────────────────────────────────────────────────────────────────────┘
```

### 7.3 Vault Backup Strategy

```
┌──────────────────────────────────────────────────────────────────────┐
│  VAULT BACKUP PROCEDURE                                               │
│                                                                       │
│  The vault file is always encrypted on disk, so backup is a simple   │
│  file copy. No special handling needed.                              │
│                                                                       │
│  Strategy:                                                           │
│  ├── Before every credential add/update/delete: copy vault.enc       │
│  │   to vault.enc.bak (single previous version)                      │
│  ├── Before key rotation: copy vault.enc to                          │
│  │   vault-pre-rotation-20260314.enc                                  │
│  ├── ICredentialVault.ExportAsync() produces a standalone encrypted   │
│  │   blob that can be stored anywhere                                │
│  └── Export requires vault to be unlocked (master password known)    │
│                                                                       │
│  Recovery:                                                           │
│  ├── From file backup: copy vault.enc.bak → vault.enc               │
│  ├── From export: ICredentialVault.ImportAsync(blob, password)       │
│  └── Master password MUST be known — no recovery without it         │
│                                                                       │
│  ⚠ CRITICAL: If master password is lost, vault data is              │
│  unrecoverable by design. There is no backdoor.                     │
└──────────────────────────────────────────────────────────────────────┘
```

### 7.4 Recovery Procedures

#### Scenario 1: Database Corruption

```
Symptoms: EF Core throws SqliteException, data queries return errors
Detection: ISystemHealthService.CheckAsync() reports DB component Unhealthy

Recovery:
1. Stop the application
2. Verify corruption: sqlite3 homemanagement.db "PRAGMA integrity_check"
3. If repairable: sqlite3 homemanagement.db ".recover" > recovered.sql
                  sqlite3 homemanagement-new.db < recovered.sql
4. If unrepairable: restore from latest backup
   cp backups/homemanagement-backup-LATEST.db homemanagement.db
5. Verify HMAC chain integrity: ChainVerifier.VerifyAsync()
6. Restart application
7. Log SettingsChanged audit event documenting the recovery
```

#### Scenario 2: Vault File Lost or Corrupted

```
Symptoms: UnlockAsync throws CryptographicException or FileNotFoundException

Recovery:
1. Check for vault.enc.bak (automatic pre-change backup)
   cp vault.enc.bak vault.enc  → try unlock
2. If .bak is also corrupt: use last ExportAsync blob
   Import via ICredentialVault.ImportAsync(blob, masterPassword)
3. If no backup exists: vault is LOST
   → Re-create vault with new master password
   → Re-enter all credentials manually
   → Update CredentialId on all machines
   → Audit: CredentialCreated events for each re-entered credential
```

#### Scenario 3: Complete Data Directory Loss

```
Recovery:
1. Restore all files from backup:
   backups/homemanagement-backup-LATEST.db → homemanagement.db
   vault.enc backup → vault.enc
   certs/ backup → certs/
   known_hosts backup → known_hosts
2. Run EF Core migrations: ServiceRegistration.InitializeDatabaseAsync()
3. Verify HMAC chain: some events since last backup are lost
   → Record a SettingsChanged audit event noting the gap
4. Re-scan all machines: IInventoryService.RefreshMetadataAsync()
5. Verify agent connectivity: IAgentGateway status check
```

### 7.5 Backup Integrity Verification

```
After each backup:
1. Open backup database in read-only mode
2. Run PRAGMA integrity_check
3. Count rows in key tables (Machines, AuditEvents, Jobs)
4. Compare counts against source database (should match ± recent writes)
5. Verify the last AuditEvent.EventHash matches the live database
6. Log backup result (size, row counts, integrity check outcome)

If verification fails:
→ Retry backup once
→ If second failure: log Error, alert user via GUI notification
```

---

## 8. Data Lifecycle & Retention

### 8.1 Retention Policies

| Table | Retention | Purge Strategy | Trigger |
|---|---|---|---|
| `Machines` | Indefinite (soft-delete) | Never hard-deleted; `IsDeleted=1` | Manual removal |
| `MachineTags` | Matches parent machine | CASCADE delete with machine | Machine hard-delete (future) |
| `PatchHistory` | 2 years | Delete rows where `TimestampUtc < now - 2y` | Scheduled maintenance job |
| `ServiceSnapshots` | 90 days | Delete where `CapturedUtc < now - 90d` | Scheduled maintenance job |
| `AuditEvents` | Indefinite (compliance) | Export to archive file, then optionally purge | Manual (admin decision) |
| `Jobs` | 1 year | Delete where `CompletedUtc < now - 1y` AND `State IN (Completed, Failed, Cancelled)` | Scheduled maintenance job |
| `JobMachineResults` | Matches parent job | CASCADE delete with job | Job purge |
| `ScheduledJobs` | Indefinite | Deleted when user removes schedule | Manual |
| `AppSettings` | Indefinite | Never deleted | — |

### 8.2 Archival Process

```
AuditEvents Archival:

1. Export: IAuditLogger.ExportAsync(query{ToUtc < cutoff}, stream, Json)
   → Produces JSON file with full event data + hashes
   
2. Verify: Run ChainVerifier on exported events
   → Confirm chain integrity before deletion
   
3. Record: Create a "chain checkpoint" audit event containing:
   - Last exported EventId
   - Last exported EventHash
   - Export file path/hash
   
4. Purge (optional): DELETE FROM AuditEvents WHERE TimestampUtc < cutoff
   → Only after verified export exists
   → NEVER automated — admin must explicitly confirm
   
5. The checkpoint event becomes the new chain anchor:
   subsequent events chain from this checkpoint's hash
```

### 8.3 Soft-Delete Semantics

```
Machine Soft-Delete Flow:

  RemoveAsync(machineId)
         │
         ▼
  UPDATE Machines SET IsDeleted = 1, UpdatedUtc = @now WHERE Id = @id
         │
         ├── MachineTags: NOT deleted (preserved for historical queries)
         ├── PatchHistory: NOT deleted (preserved for compliance)
         ├── ServiceSnapshots: NOT deleted (preserved for audit)
         ├── AuditEvents: NOT deleted (immutable by design)
         ├── JobMachineResults: NOT deleted (historical job data)
         └── Global query filter: Machine excluded from all normal queries
         
  To include soft-deleted machines in queries:
    MachineQuery { IncludeDeleted = true }
    → EF Core: .IgnoreQueryFilters() on Machines DbSet
```

---

## 9. Migration Strategy

### 9.1 EF Core Code-First Migrations

```
Migration Workflow:

  Developer makes entity change
         │
         ▼
  dotnet ef migrations add <MigrationName>
    --project src/HomeManagement.Data
    --startup-project src/HomeManagement.Gui
         │
         ▼
  Migration file generated in:
    src/HomeManagement.Data/Migrations/<timestamp>_<MigrationName>.cs
         │
         ▼
  Applied at application startup:
    ServiceRegistration.InitializeDatabaseAsync()
      → db.Database.MigrateAsync()
      → EF Core applies pending migrations sequentially
```

### 9.2 Migration Safety Rules

| Rule | Enforcement |
|---|---|
| **Never drop columns** in a migration (data loss) | Code review |
| **Always provide default values** for new non-nullable columns | EF Core `HasDefaultValue()` |
| **Add columns as nullable first**, then backfill, then make non-nullable | Two-step migration |
| **Never rename tables** (breaks existing queries during rollback) | Add new table, migrate data, drop old |
| **Test migrations on a copy of production data** before deploying | Pre-deployment checklist |
| **Backup database before any migration** | Automated in `InitializeDatabaseAsync()` |

### 9.3 Schema Version Tracking

EF Core maintains the `__EFMigrationsHistory` table automatically:

```sql
CREATE TABLE __EFMigrationsHistory (
    MigrationId    TEXT NOT NULL PRIMARY KEY,   -- e.g., "20260314100000_InitialCreate"
    ProductVersion TEXT NOT NULL                -- e.g., "8.0.11"
);
```

---

## 10. Performance Characteristics

### 10.1 Query Performance Targets

| Query | Target | Index Used |
|---|---|---|
| Get machine by ID | < 1 ms | PK |
| Search machines by hostname (exact) | < 1 ms | IX_Machines_Hostname |
| List machines with tag filter | < 10 ms | IX_MachineTags_Key_Value + PK join |
| Patch history for one machine | < 5 ms | IX_PatchHistory_MachineId_TimestampUtc |
| "Has patch X been installed on machine Y?" | < 1 ms | IX_PatchHistory_MachineId_PatchId |
| Audit events for time range | < 50 ms (10K events) | IX_AuditEvents_TimestampUtc |
| Audit trail for one machine | < 10 ms | IX_AuditEvents_TargetMachineId_TimestampUtc |
| Events by correlation ID | < 5 ms | IX_AuditEvents_CorrelationId |
| Running jobs | < 1 ms | IX_Jobs_State |
| Jobs that failed on machine X | < 5 ms | IX_JobMachineResults_MachineId_Success |
| Paged query (50 rows) | < 10 ms | Depends on filter + OFFSET/LIMIT |

### 10.2 Write Performance Characteristics

| Operation | Expected Time | Bottleneck |
|---|---|---|
| Insert 1 machine + 3 tags | < 5 ms | 4 inserts in one SaveChanges (WAL mode) |
| Insert 100 patch history rows | < 50 ms | Batch insert within single transaction |
| Insert 1 audit event (with HMAC compute) | < 10 ms | HMAC-SHA256 computation (~1 ms) + DB write |
| Update job progress (1 row) | < 2 ms | Single UPDATE |
| Vault unlock (Argon2id + AES-GCM) | ~400 ms | Argon2id KDF (intentionally slow) |

### 10.3 Database Size Projections

```
100 machines, 1 year of operation:

  Machines:           ~100 rows × 500 bytes          =    50 KB
  MachineTags:        ~300 rows × 100 bytes          =    30 KB
  PatchHistory:     ~36,000 rows × 200 bytes         =   7.2 MB
  ServiceSnapshots: ~720,000 rows × 150 bytes        = 108.0 MB  ◄── largest table
  AuditEvents:      ~120,000 rows × 400 bytes        =  48.0 MB
  Jobs:               ~3,600 rows × 300 bytes        =   1.1 MB
  JobMachineResults: ~36,000 rows × 150 bytes        =   5.4 MB
  ScheduledJobs:        ~10 rows × 500 bytes          =     5 KB
  Indexes:           ~20% of data size                =  34.0 MB
  ─────────────────────────────────────────────────────────────
  Total (approximate):                                  ~204 MB

  After 3 years (with ServiceSnapshots purged at 90 days):
  ServiceSnapshots capped at ~180,000 rows            =  27 MB
  Total (approximate):                                  ~150 MB
```

Well within SQLite's optimal range. No scaling concern anticipated for < 500 machines.

### 10.4 Concurrency Model

```
┌──────────────────────────────────────────────────────────────────────┐
│  SQLite WAL Mode Concurrency                                          │
│                                                                       │
│  Readers: Unlimited concurrent (read from main DB + WAL snapshot)    │
│  Writers: Single writer at a time (busy_timeout = 5 seconds)         │
│                                                                       │
│  EF Core DbContext Lifetime:                                         │
│  ├── Scoped (per DI scope / per operation)                            │
│  ├── Short-lived: open, query/write, SaveChanges, dispose            │
│  └── No long-held connections or transactions                         │
│                                                                       │
│  Audit Event Write Serialization:                                    │
│  ├── HMAC chain requires sequential writes                           │
│  ├── SemaphoreSlim(1) in AuditLoggerImpl                            │
│  └── Does not block reads — only serializes writes to AuditEvents    │
│                                                                       │
│  WAL Checkpoint:                                                     │
│  ├── Automatic: SQLite auto-checkpoints when WAL > 1000 pages       │
│  ├── Manual: PRAGMA wal_checkpoint(TRUNCATE) after backup             │
│  └── No intervention needed under normal load                        │
└──────────────────────────────────────────────────────────────────────┘
```
