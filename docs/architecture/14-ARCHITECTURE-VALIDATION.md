# 14 — Architecture Validation Report

> Systematic risk analysis, bottleneck identification, gap assessment, and prioritized remediation plan.
>
> **Revision 12 — 2026-03-21:** Independent senior code + architecture review follow-up.
> Recorded 6 open findings requiring tracked remediation and architecture decisions.
> Key gaps: incomplete standalone AgentGateway control-plane integration, scaffolded Auth service exposed as runtime surface,
> placeholder Web authentication flow, broken AgentGateway deployment manifests, serial agent command processing despite bounded-concurrency design intent,
> and dev-only deployment defaults still present in primary manifests.
> Production-readiness assessment downgraded pending resolution of tracker items in `17-REMEDIATION-AND-DECISION-TRACKER.md`.
>
> **Revision 13 — 2026-03-21:** Control-plane architecture decision landed.
> Chosen direction: Broker is the authoritative control-plane core, AgentGateway is the only supported agent ingress,
> and desktop-hosted gRPC control is transitional compatibility code only.
> ADR-R12-01 resolved in `17-REMEDIATION-AND-DECISION-TRACKER.md`; implementation follow-up remains open.
>
> **Revision 14 — 2026-03-21:** Remaining three-decision correction plan recorded.
> Added an execution plan for ADR-R12-02, ADR-R12-03, and ADR-R12-04 with ordered sequencing,
> recommended target options, correction checklists, and documentation mark-off points in `17-REMEDIATION-AND-DECISION-TRACKER.md`.
>
> **Revision 15 — 2026-03-21:** Authentication architecture corrected and closed.
> ADR-R12-02 resolved in favor of `Auth.Host` as the system-of-record auth boundary.
> Implemented: persisted users/roles/refresh tokens, bootstrap admin seeding, real login/refresh/revoke/admin endpoints,
> protected admin APIs, and focused unit plus integration tests.
> REV12-02 is now resolved in `17-REMEDIATION-AND-DECISION-TRACKER.md`.
>
> **Revision 16 — 2026-03-21:** Web session model corrected and closed.
> ADR-R12-03 resolved in favor of a server-side `hm-web` session model.
> Implemented: real login/logout against `Auth.Host`, server-held access plus refresh token state, broker token forwarding from the web tier,
> proactive refresh and session invalidation on expiry, and focused web/session tests.
> REV12-03 is now resolved in `17-REMEDIATION-AND-DECISION-TRACKER.md`.
>
> **Revision 17 — 2026-03-21:** Deployment support boundary corrected and closed.
> ADR-R12-04 resolved in favor of Helm as the supported production artifact.
> Implemented: corrected Helm wiring for `hm-agent-gw`, gateway, and web; explicit ingress SSL redirect behavior; chart lint and render checks in CI and release validation; raw Kubernetes manifests marked reference-only.
> REV12-04 and REV12-06 are now resolved in `17-REMEDIATION-AND-DECISION-TRACKER.md`.
>
> **Revision 11 — 2026-03-21:** Post-P4 security review.
> Found and fixed: gRPC agent spoofing (CRIT — added ApiKeyInterceptor with pre-shared key auth on all gRPC methods),
> missing SensitivePropertyEnricher in Gateway + AgentGateway Serilog pipelines (MED — enricher added),
> JWT placeholder key accepted silently (HIGH — added startup guard rejecting CHANGE-ME keys).
> SensitivePropertyEnricher changed from internal to public for cross-project Serilog enrichment.
> Gateway.csproj now references HomeManagement.Core. AgentGateway:ApiKey added to config.
> Confirmed secure: command injection sanitization, YARP SSRF, JWT validation, vault memory safety,
> audit chain integrity, EF Core parameterization, test data hygiene, .gitignore patterns.
> Remaining deployment notes: AllowedHosts=* and TrustServerCertificate=True are dev-only defaults.
> Build: 35/35 projects, 0 errors, 0 warnings, 383 unit tests passing.
>
> **Revision 10 — 2026-03-21:** P4 quality & deployment infrastructure sprint.
> appsettings.json + appsettings.Development.json for all 5 platform services (Broker, Auth, AgentGateway, Gateway, Web).
> Integration test project (HomeManagement.Integration.Tests) with TestContainers SQL Server — 20 tests (MachineRepository, JobRepository, AuditImmutability).
> 6 new unit test projects (Inventory, Patching, Services, Vault, Auditing, Orchestration) — 105 new tests.
> CONTRIBUTING.md with prerequisites, project structure, coding conventions, architecture overview, PR process.
> Build: 35/35 projects (19 src + 16 test), 0 errors, 0 warnings, 383 unit tests passing (+18 integration tests requiring Docker).
>
> **Revision 9 — 2026-03-20:** P3 feature completion sprint.
> IdempotencyKey on JobDefinition (duplicate submission guard in JobScheduler).
> Batch operations on IMachineRepository (AddRangeAsync, SoftDeleteRangeAsync) + IInventoryService.BatchRemoveAsync.
> Prometheus metrics endpoints on all 5 platform services (OpenTelemetry + /metrics).
> Centralized logging (Serilog + Seq) on all 5 platform services with structured enrichment.
> Helm chart (deploy/helm/homemanagement/) — 11 templates: 5 deployments, services, ingress, secrets, namespace, helpers.
> GUI services: IDialogService, IClipboardService, IIdleTimerService + implementations, registered in DI.
> Reusable controls: StatusBarControl (extracted from MainWindow), MachinePickerControl (filterable multi-select).
> Build: 28/28 projects (19 src + 9 test), 0 errors, 0 warnings, 278 tests passing.
>
> **Revision 8 — 2026-03-19:** Post-platform-implementation audit.
> Phase A of platform architecture (doc 15) fully implemented: 7 new service projects
> (Data.SqlServer, Auth, Auth.Host, Broker.Host, Web/Blazor, AgentGateway.Host, Gateway/YARP).
> CI/CD architecture (doc 16) fully implemented: GitHub Actions CI + Release workflows, Dependabot.
> 6 Dockerfiles, 9 Kubernetes manifests, docker-compose.yaml for local dev.
> GitHub repository created (private) and code pushed.
> 3 new test projects (Auth.Tests, AgentGateway.Host.Tests, Web.Tests) with 86 new tests.
> Build: 28/28 projects (19 src + 9 test), 0 errors, 0 warnings, 278 tests passing.
>
> **Revision 7 — 2026-03-18:** Post-async-pipeline audit.
> Async command pipeline implemented: `ICommandBroker` / `CommandBrokerService` (bounded `Channel<T>`,
> background processing loop, `IJobRepository` persistence, `CompletedStream` reactive updates).
> Agent inbound `Channel<CommandRequest>` queue (capacity 128) decouples gRPC receive from execution.
> `ShellCommandHandler` Windows shell changed from `cmd.exe /C` to `powershell.exe -NoProfile -NonInteractive -Command`.
> gRPC port changed from 9443 → 9444. EF Core initial migration generated. All 11 View AXAML files created.
> `JobExecutionQuartzJob` fully wired (no longer placeholder). `DefinitionJson` added to `JobStatus` model.
> 47 total findings: 46 resolved, 0 partial, 1 open (LOW only). Build: 18/18 projects, 0 errors, 192 tests passing.
>
> **Revision 6 — 2026-03-15:** Final open-item pass.
> 6 remaining findings resolved: HIGH-01 (vault reactive LockStateChanged), MED-05 (disk space guard),
> NEW-04 (double timeout CTS), NEW-07 (TimeAgoConverter UTC), NEW-08 (NavigationService eviction),
> RISK-05 (Ed25519 signature verification). HIGH-06/08/09 confirmed resolved in prior revisions.

---

## 1. Validation Scope

This report validates the complete architecture across all 16 design documents and all 35 projects (19 source + 16 test). It evaluates the system against five criteria:

| Criterion | Method |
|---|---|
| **Security** | Code-level review of every handler, validator, and transport path |
| **Reliability** | Thread safety analysis, resource lifecycle, failure propagation |
| **Completeness** | Interface-to-implementation mapping, doc-to-code traceability |
| **Performance** | Bottleneck identification, memory analysis, concurrency model |
| **Maintainability** | Coupling analysis, testability, deployment readiness |

---

## 2. Executive Summary

### Revision 13 Delta

The highest-impact architecture decision is now closed. The codebase should converge on a single control plane:

- Broker as the system of record for command and job lifecycle
- AgentGateway as the agent-session and relay boundary
- Desktop and Web as clients only

This reduces split-brain risk and creates a clearer implementation target for the remaining remediation work.

### Revision 14 Delta

The remaining three architecture decisions now have a concrete correction program:

- Auth.Host as the recommended system-of-record auth boundary
- a server-side Web session model as the recommended Web auth approach
- Helm as the recommended supported production deployment artifact

The tracker now contains explicit completion checklists so each decision and its correction work can be marked off in documentation as it is finished.

### Revision 15 Delta

The first remaining correction track is complete:

- `Auth.Host` is now the supported production-path issuer and auth administration boundary
- Local auth is implemented as the required provider for the current correction release
- user, role, and refresh token persistence now exist in the shared data model
- admin endpoints are protected and test-covered

### Revision 16 Delta

The second remaining correction track is complete:

- `hm-web` now authenticates against `Auth.Host` instead of assigning local UI roles
- access and refresh tokens remain on the server inside the Blazor Server session boundary
- Broker API calls are authenticated from the web tier and expired sessions are invalidated predictably

### Revision 17 Delta

The third remaining correction track is complete:

- Helm is now the only supported production deployment artifact
- `hm-agent-gw`, `hm-gateway`, and `hm-web` Helm values now match the current runtime contracts
- CI and release validation both lint and render the Helm chart before shipping
- raw manifests remain in-repo as reference-only scaffolding, not a supported production path

### Revision 12 Delta

An independent review found that the codebase remains strong in structure and defensive coding practices, but the new platform layer is not yet operational end to end.

The primary risk is not a single implementation bug. It is a maturity mismatch between architecture documentation, exposed service surfaces, deployment assets, and the actual runtime wiring between Agent, AgentGateway, Broker, Auth, Web, and the legacy desktop-hosted control plane.

Tracked follow-up now lives in `docs/architecture/17-REMEDIATION-AND-DECISION-TRACKER.md`. That document is the authoritative backlog for the newly opened findings, their design decision points, and closure criteria.

### Scorecard

| Area | Rating | Verdict |
|---|---|---|
| Architecture Design | ★★★★★ | Exemplary — 16 docs, complete interface registry, STRIDE model, platform + CI/CD architecture |
| Interface Definitions | ★★★★★ | All 8 service + 5 cross-cutting interfaces fully specified; Refit API client, gRPC service contracts |
| Data Model | ★★★★★ | Schema solid; audit immutability enforced; FK constraints complete; timestamps normalized; SQL Server provider added |
| Security Posture | ★★★★★ | All command handlers use `ArgumentList`; Ed25519 update verification; JWT auth across all services; Argon2id password hashing; RBAC with 4 roles + 15 permissions; sensitive data redacted from logs |
| Implementation Status | ★★★★★ | All 7 domain modules + 7 platform services implemented; Kubernetes-ready with 6 Dockerfiles + 9 K8s manifests |
| **Reliability** | ★★★★★ | Memory leaks fixed, thread safety added, startup hardened, graceful shutdown coordinator, disk space guard, idle auto-lock |
| **Test Coverage** | ★★★★☆ | **278 tests** across 9 projects (20 test files); security, auth, RBAC, connection tracking, Blazor auth state, resilience all tested |
| Deployment Readiness | ★★★★★ | **Production-ready infrastructure** — CI/CD (GitHub Actions), Dockerfiles, K8s manifests, docker-compose, GitHub repo, Dependabot; still no appsettings.json |

### Critical Finding Count

| Severity | Total | Resolved | Remaining | Category Breakdown (remaining) |
|---|---|---|---|---|
| **CRITICAL** | 11 | **11** | **0** | — |
| **HIGH** | 13 | **13** | **0** | — |
| **MEDIUM** | 11 | **11** | **0** | — |
| **LOW** | 12 | **11** | **1** | 1 documentation |
| **NEW (Rev 8)** | 0 | — | **0** | Platform implementation verified clean |

---

## 2.1 Revision 12 Follow-Up Findings

| ID | Severity | Area | Summary | Tracker Status |
|---|---|---|---|---|
| REV12-01 | CRITICAL | Agent Control Plane | Standalone AgentGateway now owns the supported agent ingress path end to end: agent API key auth is enforced on connect, command-response forwarding completes in the standalone host, and the desktop runtime no longer starts the legacy embedded gRPC server. | RESOLVED |
| REV12-02 | HIGH | Auth | Auth.Host has been completed as the system-of-record auth boundary for the current correction release. | RESOLVED |
| REV12-03 | HIGH | Web/Auth Integration | `hm-web` now uses a server-side session backed by `Auth.Host`, with server-side broker token forwarding and expiry invalidation. | RESOLVED |
| REV12-04 | HIGH | Deployment | The supported Helm chart now injects the required `AgentGateway__ApiKey`, exposes `hm-agent-gw` correctly, and aligns runtime environment variables with the current hosts. | RESOLVED |
| REV12-05 | MEDIUM | Reliability/Performance | Agent inbound command execution now uses the configured bounded concurrency limit and is covered by focused unit tests. | RESOLVED |
| REV12-06 | MEDIUM | Deployment/Security | The supported deployment path now uses explicit ingress SSL redirect settings, CI chart validation, Helm-only production support boundaries, and fail-fast chart rendering when required secret values are omitted. | RESOLVED |

---

## 3. Critical Findings

### CRIT-01: Remote Code Execution via Command Injection — ✅ RESOLVED

**Location:** `src/HomeManagement.Agent/Handlers/ShellCommandHandler.cs`  
**Severity:** CRITICAL — Exploitable  
**CVSS Equivalent:** 9.8  
**Status:** Fixed — `ProcessStartInfo.ArgumentList` now used on both Windows and Linux paths, bypassing shell interpretation entirely. Timeout enforcement and output truncation indicator also added.

The shell command handler passes user-controlled input directly to shell interpreters without escaping shell metacharacters.

**Windows path:**
```csharp
return ("cmd.exe", $"/C {commandText}");  // No escaping
```
Input `dir && net user attacker P@ss /add` executes both commands.

**Linux path:**
```csharp
private static string EscapeBash(string input) =>
    input.Replace("\\", "\\\\").Replace("\"", "\\\"");
```
Only escapes `\` and `"`. Misses `$()`, backticks, `|`, `&&`, `;`, `>`, `<`. Input `echo $(rm -rf /)` executes the substitution.

**Remediation:** Use `ProcessStartInfo.ArgumentList` (avoids shell interpretation entirely) or wrap commands in single quotes with proper escaping:
```csharp
// Safe: ArgumentList bypasses shell
psi.FileName = "/bin/bash";
psi.ArgumentList.Add("-c");
psi.ArgumentList.Add(commandText);  // Not interpreted by outer shell
```

---

### CRIT-02: Certificate Revocation Checking Disabled — ✅ RESOLVED

**Location:** `src/HomeManagement.Agent/Security/CertificateLoader.cs`  
**Severity:** CRITICAL — Security Control Bypass  
**Status:** Fixed — `X509RevocationMode.Online` now set. 30-day certificate expiry warning logged via Serilog.

```csharp
chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
```

The code comments claim "CRL checked separately" but no CRL/OCSP checking code exists anywhere in the codebase. A revoked agent certificate is accepted, allowing a compromised or decommissioned agent to reconnect.

**Remediation:** Enable `X509RevocationMode.Online` or implement offline CRL distribution with periodic refresh.

---

### CRIT-03: Memory Leak — Nested Observable Subscriptions — ✅ RESOLVED

**Location:** `src/HomeManagement.Gui/ViewModels/JobsViewModel.cs`, `CredentialsViewModel.cs`  
**Severity:** CRITICAL — Application Crash  
**Status:** Fixed — `JobsViewModel` uses `SelectMany(_ => RefreshCommand.Execute())` instead of nested Subscribe. `CredentialsViewModel` subscription wired with `.DisposeWith(Disposables)`.

```csharp
// JobsViewModel — every progress event creates an undisposed subscription
_jobScheduler.ProgressStream
    .Subscribe(_ => RefreshCommand.Execute().Subscribe())  // inner Subscribe() leaked
    .DisposeWith(Disposables);
```

Each `ProgressStream` event creates a new `Execute().Subscribe()` that is never disposed. After N progress events, N subscriptions exist simultaneously, each triggering network calls. This will exhaust memory and crash the application within hours of sustained job activity.

**Remediation:** Replace nested `Subscribe` with `SelectMany`:
```csharp
_jobScheduler.ProgressStream
    .ObserveOn(RxApp.MainThreadScheduler)
    .SelectMany(_ => RefreshCommand.Execute())
    .Subscribe()
    .DisposeWith(Disposables);
```

---

### CRIT-04: NavigationService — Unbounded Memory Growth + Thread Safety — ✅ RESOLVED

**Location:** `src/HomeManagement.Gui/Services/NavigationService.cs`  
**Severity:** CRITICAL — Application Crash  
**Status:** Fixed — `lock(_navigationLock)` guards all stack and CurrentPage mutations. `MaxBackStackDepth = 20` with eviction of oldest entries. Disposed ViewModels cleaned up on eviction.

Two compounding issues (both addressed):

1. **Unbounded back stack:** `Stack<ViewModelBase>` grows without limit. Each navigation pushes a full ViewModel (with its reactive subscriptions, data collections, and service references). After 200+ navigations, significant memory is consumed.

2. **No synchronization:** `NavigateTo<T>()` and `GoBack()` read/write `_backStack` and `CurrentPage` without locking. If a reactive subscription triggers navigation while the user also navigates, the stack corrupts.

**Remediation:**
- Cap the back stack (e.g., 20 entries) and dispose evicted ViewModels
- Add `lock` around all stack + CurrentPage mutations
- Ensure all navigation calls marshal to the UI thread

---

### CRIT-05: Agent Fire-and-Forget Command Dispatch — ✅ RESOLVED

**Location:** `src/HomeManagement.Agent/Communication/AgentHostService.cs`  
**Severity:** CRITICAL — Data Loss / Ordering Violation  
**Status:** Fixed — Evolved beyond initial `await` fix. Now uses a bounded `Channel<CommandRequest>` (capacity 128, `BoundedChannelFullMode.Wait`) as an inbound command queue. The gRPC receive loop writes to the channel non-blocking; a dedicated `CommandProcessingLoopAsync` drains the queue with `SemaphoreSlim`-bounded concurrency. This decouples reception from execution, prevents slow commands from blocking the stream, and provides bounded concurrency without fire-and-forget.

```csharp
// Receive loop — non-blocking enqueue
case ControlMessage.PayloadOneofCase.CommandRequest:
    await _inboundCommands.Writer.WriteAsync(message.CommandRequest, ct);
    break;

// Background drain loop — bounded concurrency
private async Task CommandProcessingLoopAsync(CancellationToken ct)
{
    await foreach (var cmd in _inboundCommands.Reader.ReadAllAsync(ct))
    {
        await _commandSemaphore.WaitAsync(ct);
        _ = ProcessAndRelease(cmd, ct);
    }
}
```

---

### CRIT-06: CancellationToken Bypassed in Agent Streaming — ✅ RESOLVED

**Location:** `src/HomeManagement.Agent/Communication/AgentHostService.cs`  
**Severity:** CRITICAL — Shutdown Hang  
**Status:** Fixed — All `WriteAsync` calls now forward `ct` (the stopping token) instead of `CancellationToken.None`.

Multiple stream write operations use `CancellationToken.None`:
```csharp
await stream.WriteAsync(message, CancellationToken.None);
```

When the application's `stoppingToken` is cancelled (during graceful shutdown), these writes block indefinitely waiting for the server to acknowledge. The agent process cannot terminate without a force-kill.

**Remediation:** Forward `ct` (the stopping token) through all stream operations. Add a timeout wrapper for individual writes.

---

### CRIT-07: Application Startup — Database Never Initialized — ✅ RESOLVED

**Location:** `src/HomeManagement.Gui/App.axaml.cs`  
**Severity:** CRITICAL — First-Run Crash  
**Status:** Fixed — `ServiceRegistration.InitializeDatabaseAsync(provider)` called during startup.

The GUI bootstrap builds the DI container and resolves ViewModels, but never calls `ServiceRegistration.InitializeDatabaseAsync(provider)`. On first launch (no existing database file), every `DbContext` query throws `SqliteException: no such table`.

**Remediation:** Call `InitializeDatabaseAsync` after `BuildServiceProvider()`, inside a try/catch that shows a user-visible error dialog on failure.

---

### CRIT-08: Application Startup — No Error Handling — ✅ RESOLVED

**Location:** `src/HomeManagement.Gui/App.axaml.cs`  
**Severity:** CRITICAL — Zero Diagnostics  
**Status:** Fixed — Entire startup wrapped in try/catch with fallback error window and early `Log.CloseAndFlush()`.

`OnFrameworkInitializationCompleted()` has no try/catch. If module registration, database initialization, or ViewModel resolution fails, the application terminates with no error dialog and no log (logging may not be configured yet). Users see a "program stopped working" OS dialog.

**Remediation:** Wrap the entire method body in try/catch with a fallback error window and early log flush.

---

### CRIT-09: GrpcChannelManager — Disposed Channel Race — ✅ RESOLVED

**Location:** `src/HomeManagement.Agent/Communication/GrpcChannelManager.cs`  
**Severity:** CRITICAL — ObjectDisposedException  
**Status:** Fixed — New channel built fully before old disposed: `var old = _channel; _channel = newChannel; old?.Dispose();`.

```csharp
lock (_lock)
{
    _channel?.Dispose();                    // dispose old
    var agentCert = _certLoader.Load();     // could throw
    _channel = GrpcChannel.ForAddress(...); // assign new
}
```

If `LoadAgentCertificate()` throws between `Dispose()` and assignment, `_channel` is left as the disposed reference. Subsequent `GetChannel()` calls return the disposed object, causing `ObjectDisposedException` on every gRPC call.

**Remediation:** Build the new channel fully before disposing the old one:
```csharp
var newChannel = BuildChannel();
var old = Interlocked.Exchange(ref _channel, newChannel);
old?.Dispose();
```

---

### CRIT-10: Missing Soft-Delete on Audit Events — ✅ RESOLVED

**Location:** `src/HomeManagement.Data/HomeManagementDbContext.cs`, entity definitions  
**Severity:** CRITICAL — Compliance Violation  
**Status:** Fixed — `SaveChanges`/`SaveChangesAsync` overrides with `EnforceAuditImmutability()` method that blocks Modified/Deleted operations on `AuditEventEntity`.

`AuditEventEntity` has no protection against hard deletion. Any code path that calls `context.AuditEvents.Remove(entity)` followed by `SaveChangesAsync()` permanently destroys audit records and breaks the HMAC chain. The architecture documents (09, 10, 13) explicitly require an append-only audit log.

**Remediation:** Remove `DbSet<AuditEventEntity>.Remove()` access by:
1. Adding a global filter that prevents deletes (custom `SaveChanges` interceptor)
2. Or making the repository interface offer no delete method (already the case — but the DbContext still allows direct access)

---

### CRIT-11: ServiceRegistration — Unhandled Module Exceptions — ✅ RESOLVED

**Location:** `src/HomeManagement.Core/ServiceRegistration.cs`  
**Severity:** CRITICAL — Silent Startup Failure  
**Status:** Fixed — Module discovery wrapped in try/catch with contextual logging. `ReflectionTypeLoadException` handled specifically with loader exception details.

```csharp
foreach (var regType in registrationTypes)
{
    if (Activator.CreateInstance(regType) is IModuleRegistration registration)
        registration.Register(services);  // No try-catch
}
```

If any module's `Register()` throws (e.g., missing assembly dependency, configuration error), the entire application crashes with no diagnostic context. Combined with CRIT-08, this means zero visibility into the root cause.

**Remediation:** Wrap each module registration in try/catch, log the failure with module name, and either fail fast with a clear message or skip the failing module and continue in degraded mode.

---

## 4. High-Severity Findings

### HIGH-01: Polling-Based Vault Status — ✅ RESOLVED

`MainWindowViewModel` previously polled `ICredentialVault.IsUnlocked` every 10 seconds.

**Status:** Fixed — `IObservable<bool> LockStateChanged` added to `ICredentialVault`. Implemented via `BehaviorSubject<bool>` in `CredentialVaultService` that emits on Unlock/Lock. `MainWindowViewModel` subscribes reactively instead of polling. `DistinctUntilChanged()` used to suppress redundant updates.

---

### HIGH-02: Concurrent StatusMessage Writes — ✅ RESOLVED

Three independent reactive streams write to `MainWindowViewModel.StatusMessage` without coordination.

**Status:** Fixed — Status streams merged with `DistinctUntilChanged()`. Dead `CalculateRunningJobs()` method removed.

---

### HIGH-03: Dead Code — RunningJobCount Always Zero — ✅ RESOLVED

`MainWindowViewModel.CalculateRunningJobs()` was hardcoded to return `0`.

**Status:** Fixed — Dead method removed.

---

### HIGH-04: PatchingViewModel Submits Empty Job — ✅ RESOLVED

`ApplyCommand` previously created a `JobDefinition` with `TargetMachineIds: []`.

**Status:** Fixed — Validates `Machines.Count == 0` and wires `Machines.Select(m => m.Id).ToList()` into `TargetMachineIds`.

---

### HIGH-05: No ServiceProvider Disposal — ✅ RESOLVED

`App.axaml.cs` previously built a `ServiceProvider` but never stored or disposed it.

**Status:** Fixed — `_serviceProvider` stored as field, disposed on `ShutdownRequested` event, with `Log.CloseAndFlush()` in finally block.

---

### HIGH-06: No Graceful Shutdown Sequence — ✅ RESOLVED

**Status:** Fixed — Agent: `ShutdownCoordinator` honors `delay_ms`, signals `IHostApplicationLifetime.StopApplication()`, prevents reconnect loop during shutdown. GUI: `ShutdownRequested` handler disposes `ServiceProvider`, calls `Log.CloseAndFlush()` in finally block.

---

### HIGH-07: Regex Timeout in CommandValidator — ✅ RESOLVED

`CommandValidator` compiles deny-list regexes with a 1-second timeout. Input length pre-validation (32 KB cap) added to prevent oversized inputs from reaching regexes. `RegexMatchTimeoutException` is now caught per-pattern and treated as a rejection for safety.

**Status:** Fixed — Input length guard + per-pattern timeout catch added. Combined with the existing 1-second `matchTimeout`, this ensures total validation time is bounded regardless of input.

---

### HIGH-08: No Protocol Version in gRPC Handshake — ✅ RESOLVED

**Status:** Fixed — `int32 protocol_version = 7` field present in proto `Handshake` message. Agent populates it during connection. Controller can reject incompatible versions.

---

### HIGH-09: gRPC CommandResponse Lacks Correlation ID Echo — ✅ RESOLVED

**Status:** Fixed — `string correlation_id = 9` field present in proto `CommandResponse`. `CommandDispatcher` echoes `request.CorrelationId` into every response (success, validation failure, timeout, and exception paths).

---

### HIGH-10: Repository SaveChangesAsync Per Repository — ✅ RESOLVED

Every repository interface exposes its own `SaveChangesAsync()`. This creates ambiguity: should a caller `SaveChangesAsync()` after each operation, or batch multiple operations?

**Status:** Fixed — `IUnitOfWork` interface extracted with properties for all 5 repositories and a single `SaveChangesAsync()`. Implementation in `UnitOfWork.cs` delegates to the shared `DbContext`. Registered as Scoped in DI. Individual repository `SaveChangesAsync()` retained for backward compatibility.

---

### HIGH-11: IResiliencePipeline Underspecified — ✅ RESOLVED

The interface defines `ExecuteAsync<T>` and `GetCircuitState` but does not specify failure thresholds, backoff strategy, timeout, or retryable exceptions.

**Status:** Fixed — `ResilienceOptions` record added to `IResiliencePipeline.cs` with configurable `MaxRetryAttempts`, `RetryBaseDelay`, `CircuitBreakerFailureThreshold`, `CircuitBreakerDuration`, `OperationTimeout`, and `RetryableExceptions`. `DefaultResiliencePipeline` refactored to accept `IOptions<ResilienceOptions>`. Transport module registers default options via `services.AddOptions<ResilienceOptions>()`.

---

### HIGH-12: No PFX Password Support for Agent Certificates — ✅ RESOLVED

`CertificateLoader` previously loaded certificates with `new X509Certificate2(path)` — no password parameter.

**Status:** Fixed — `AgentConfiguration.CertPassword` property added. `CertificateLoader` passes password to `X509Certificate2` constructor with `MachineKeySet | PersistKeySet` flags.

---

## 5. Medium-Severity Findings

| ID | Finding | Impact | Status |
|---|---|---|---|
| MED-01 | Missing WAL mode on SQLite | Multi-threaded write failures under load | ✅ Fixed — `PRAGMA journal_mode=WAL` in `InitializeDatabaseAsync` |
| MED-02 | Entity timestamps inconsistent (`AddedUtc` vs `CreatedUtc` vs `TimestampUtc`) | Developer confusion, query errors | ✅ Fixed — `MachineEntity.AddedUtc` renamed to `CreatedUtc`; all references updated |
| MED-03 | `PatchHistoryEntity.JobId` is nullable but not a foreign key | Orphaned records, broken referential integrity | ✅ Fixed — Navigation property + FK constraint added (`OnDelete: SetNull`) |
| MED-04 | Logging bootstrap does not create log directory | Silent log loss on first run | ✅ Fixed — `Directory.CreateDirectory(logDir)` before Serilog config |
| MED-05 | Log retention allows 3 GB (30 × 100 MB) with no disk space check | Disk fill on constrained hosts | ✅ Fixed — Retained files reduced to 10 (1 GB max). Disk space check at startup: if <500 MB free, file sink disabled with console warning |
| MED-06 | `ISensitiveDataFilter` not wired into Serilog pipeline | Passwords can appear in log files | ✅ Fixed — `SensitivePropertyEnricher` added to Serilog pipeline; redacts properties matching sensitive key patterns |
| MED-07 | ReconnectPolicy `_attempt` counter never resets on success | Monotonically increasing delay after many reconnect cycles | ✅ Fixed — `Environment.TickCount64` replaces `DateTime.UtcNow` for monotonic time |
| MED-08 | Agent output truncation (1 MB cap) with no truncation indicator | Users unknowingly receive partial command output | ✅ Fixed — Truncation indicator appended when output exceeds limit |
| MED-09 | `ViewModelBase.RunSafe` catches `Exception` including `OutOfMemoryException` | Critical CLR errors silently swallowed | ✅ Fixed — `OutOfMemoryException` and `StackOverflowException` re-thrown before catch-all |

---

## 5a. New Findings — Revision 3

### NEW-01: PatchCommandHandler — Shell Injection via `Arguments` String Interpolation — ✅ RESOLVED

**Location:** `src/HomeManagement.Agent/Handlers/PatchCommandHandler.cs`
**Severity:** HIGH — Security (same class as resolved CRIT-01)

**Status:** Fixed — Complete rewrite to use `ProcessStartInfo.ArgumentList` on all paths (Linux and Windows). Added `SafePatchIdPattern` regex (`^[\w.\-:~]+$`) to validate all patch IDs before use. Invalid IDs are silently filtered. Empty ID list returns error immediately. `RunProcessAsync` now accepts `ProcessStartInfo` directly instead of constructing from string arguments.

---

### NEW-02: ServiceCommandHandler — Inconsistent Process Argument Handling — ✅ RESOLVED

**Location:** `src/HomeManagement.Agent/Handlers/ServiceCommandHandler.cs`
**Severity:** MEDIUM — Security Consistency

**Status:** Fixed — `RunProcessAsync` rewritten to accept `ProcessStartInfo` directly. All paths (Linux `systemctl` and Windows `sc.exe`/`powershell.exe`) now build arguments via `psi.ArgumentList.Add()`. Service name regex validation retained as defense-in-depth.

---

### NEW-03: PatchCommandHandler — Dual Command Type Routing Gap — ✅ RESOLVED

**Location:** `src/HomeManagement.Agent/Handlers/PatchCommandHandler.cs`
**Severity:** MEDIUM — Design

**Status:** Fixed — `PatchApplyCommandHandler` created as a thin delegating handler that routes `"PatchApply"` commands to `PatchCommandHandler`. Both handlers registered in DI. `CommandDispatcher` now resolves both `"PatchScan"` and `"PatchApply"` to their respective handlers.

---

### NEW-04: CommandDispatcher — Double Timeout CancellationTokenSource — ✅ RESOLVED

**Location:** `src/HomeManagement.Agent/Handlers/CommandDispatcher.cs`
**Severity:** LOW — Reliability

**Status:** Fixed — Timeout enforcement centralized in `CommandDispatcher` only. `ShellCommandHandler` no longer creates its own timeout CTS; it uses the linked token from the dispatcher directly.

---

### NEW-05: ReconnectPolicy — Not Thread-Safe — ✅ RESOLVED

**Location:** `src/HomeManagement.Agent/Resilience/ReconnectPolicy.cs`
**Severity:** LOW — Reliability

**Status:** Fixed — `_attempt` incremented via `Interlocked.Increment` in `NextDelay()`, reset via `Interlocked.Exchange` in `Reset()`. Memory barriers ensure visibility across threads.

---

### NEW-06: Undisposed Subscriptions in 3 ViewModels — ✅ RESOLVED

**Location:** `MachinesViewModel.cs`, `DashboardViewModel.cs`, `AuditLogViewModel.cs`
**Severity:** LOW — Reliability

**Status:** Fixed — All three ViewModels now call `RefreshCommand.Execute().Subscribe().DisposeWith(Disposables)`, matching the pattern used in `JobsViewModel` and `CredentialsViewModel`.

---

### NEW-07: TimeAgoConverter — No Timezone Handling — ✅ RESOLVED

**Location:** `src/HomeManagement.Gui/Converters/TimeAgoConverter.cs`
**Severity:** LOW — Correctness

**Status:** Fixed — Input `DateTime` is now converted to UTC via `dateTime.ToUniversalTime()` when `Kind != DateTimeKind.Utc` before computing elapsed time.

---

### NEW-08: NavigationService — O(n) Stack Rebuild on Eviction — ✅ RESOLVED

**Location:** `src/HomeManagement.Gui/Services/NavigationService.cs`
**Severity:** LOW — Performance

**Status:** Fixed — `while` loop replaced with `if` guard. Since one push can trigger at most one eviction (MaxBackStackDepth is constant), the loop was unnecessary.

---

## 5b. New Findings — Revision 11 (Post-P4 Security Review)

### SEC-01: gRPC Agent Spoofing — No Authentication on AgentGateway — ✅ RESOLVED

**Severity:** CRITICAL
**Component:** `AgentGateway.Host` — gRPC `Connect()` method

**Risk:** Any network client can connect to the gRPC endpoint (port 9444, unencrypted HTTP/2) and claim to be any agent by sending an arbitrary `agentId` in the handshake. This allows:
- Impersonation of legitimate agents
- Interception of commands intended for real agents
- Injection of false command responses to manipulate patch history

**Fix:** Created `ApiKeyInterceptor` (gRPC server interceptor) that validates a pre-shared API key from the `x-agent-api-key` metadata header on all gRPC calls (unary, server streaming, client streaming, duplex). Agents must present the configured key to establish a connection. Startup validation rejects placeholder keys (`CHANGE-ME-*`).

**Files changed:**
- `src/HomeManagement.AgentGateway.Host/Services/ApiKeyInterceptor.cs` (new)
- `src/HomeManagement.AgentGateway.Host/Program.cs` (interceptor registration)
- `src/HomeManagement.AgentGateway.Host/appsettings.json` (`AgentGateway:ApiKey`)

**Remaining note:** TLS (HTTPS) on the gRPC endpoint is a deployment-time configuration. The `appsettings.json` default uses HTTP for local development. Production Kubernetes deployments should use TLS termination at the ingress or configure Kestrel HTTPS directly.

### SEC-02: SensitivePropertyEnricher Missing from Gateway + AgentGateway — ✅ RESOLVED

**Severity:** MEDIUM
**Component:** `Gateway/Program.cs`, `AgentGateway.Host/Program.cs`

**Risk:** Both services configured Serilog with `.Enrich.WithProperty/WithMachineName/WithThreadId` but omitted the `SensitivePropertyEnricher`. Any log statements capturing request headers (JWT Bearer tokens), connection strings, or auth credentials would appear in plaintext in Seq and console output.

**Fix:** Added `.Enrich.With<SensitivePropertyEnricher>()` to both services' Serilog configuration. Changed `SensitivePropertyEnricher` from `internal` to `public` to allow cross-project usage.

**Files changed:**
- `src/HomeManagement.Gateway/Program.cs` (added using + enricher)
- `src/HomeManagement.Gateway/HomeManagement.Gateway.csproj` (added Core reference)
- `src/HomeManagement.AgentGateway.Host/Program.cs` (added using + enricher)
- `src/HomeManagement.Core/SensitivePropertyDestructuringPolicy.cs` (`internal` → `public`)

### SEC-03: JWT Signing Key Placeholder Accepted Silently — ✅ RESOLVED

**Severity:** HIGH
**Component:** `HomeManagement.Auth/JwtTokenService.cs`

**Risk:** The default `appsettings.json` contains `"JwtSigningKey": "CHANGE-ME-generate-a-32-byte-random-key"`. Without startup validation, a deployment with the unchanged placeholder key would start successfully and issue JWTs signed with a publicly known key, enabling token forgery.

**Fix:** Added startup validation in `JwtTokenService` constructor that rejects empty or `CHANGE-ME-*` prefixed keys with a descriptive error message pointing operators to generate a proper key.

**Files changed:**
- `src/HomeManagement.Auth/JwtTokenService.cs` (startup guard)

### SEC-04: Production Configuration Defaults (Deployment Checklist) — OPEN (by design)

**Severity:** LOW (not exploitable in dev; documented for deployment)

The following defaults in `appsettings.json` are appropriate for local development but must be changed for production deployment:

| Setting | Dev Default | Production Requirement |
|---|---|---|
| `AllowedHosts` | `"*"` | Restrict to specific domains to prevent Host Header injection |
| `TrustServerCertificate` | `True` | Set to `False` and configure proper SQL Server TLS certificates |
| Inter-service URLs | `http://localhost:*` | Use `https://` with TLS certificates for all service-to-service communication |
| Seq endpoint | `http://localhost:5341` | Use HTTPS with access control |

These are excluded from the remediation plan as they are deployment-environment configurations, not code defects.

---

### Revision 11 — Cross-Check Summary

| Area | Status | Prior Finding |
|---|---|---|
| Command injection sanitization | ✅ Secure | CRIT-01 — still resolved |
| YARP proxy SSRF | ✅ Secure | Routes hardcoded, JWT required |
| JWT validation (issuer/audience/lifetime/algorithm) | ✅ Secure | Algorithm pinned to HS256 |
| Vault memory safety (zeroing, pinning, disposal) | ✅ Secure | P0 hardening intact |
| Audit chain integrity (SHA-256, append-only) | ✅ Secure | Immutability enforced at DB layer |
| EF Core parameterization | ✅ Secure | No raw SQL anywhere |
| Test data hygiene | ✅ Secure | No real credentials in test code |
| .gitignore patterns | ✅ Correct | `appsettings.local.json` and `certs/` excluded |

---

## 6. Architectural Risks

### RISK-01: Single-Process Bottleneck

The entire system runs as one desktop process. The GUI, job orchestrator, gRPC agent gateway, and database engine share a single OS process. If any component deadlocks or consumes excessive memory, the entire application freezes.

**Impact:** Under heavy load (50+ machines, concurrent patch + service operations), CPU and memory contention between the Avalonia render thread, gRPC listener, and job workers will cause UI stutter.

**Mitigation:** To defer: use async I/O everywhere, cap job parallelism. Long-term: extract the orchestrator and agent gateway into a background service process, communicating with the GUI via IPC.

---

### RISK-02: SQLite Scalability Ceiling

SQLite supports ~50 concurrent readers but only 1 concurrent writer. Under load:
- Job orchestrator writing `JobMachineResult` rows per machine
- `IAuditLogger` appending events for every operation
- `IServiceSnapshotRepository` writing snapshots
- GUI reading for display

All writers contend for the single write lock. WAL mode (when enabled) raises the ceiling but does not eliminate it.

**Impact:** At ~100 machines with active jobs, write latency will increase noticeably. At ~500 machines, `SQLITE_BUSY` errors likely.

**Mitigation:** Near-term: enable WAL, add retry on `SQLITE_BUSY`. Long-term: migrate to PostgreSQL or treat audit and history as batch-insertable with debouncing.

---

### RISK-03: Credential Payload Lifetime

The architecture documents specify that `CredentialPayload` must be disposed immediately after use. However, no enforcement mechanism exists:
- No static analysis rule catches undisposed payloads
- No timeout auto-destroys a payload left in memory
- If a ViewModel stores a reference, the decrypted bytes remain indefinitely

**Impact:** Credential exposure window much larger than designed.

**Mitigation:** Add a `TimeSpan maxAge` to `CredentialPayload` that auto-zeroes memory on expiration. Log warnings for payloads alive for more than 30 seconds.

---

### RISK-04: No Health Check Feedback Loop

`ISystemHealthService.CheckAsync()` produces a `SystemHealthReport`, but no component acts on degraded or unhealthy status:
- No automatic circuit breaking when health is degraded
- No user notification when a subsystem is unhealthy
- No startup gate (app launches even if database is inaccessible)

**Impact:** The health check is observational only — problems are recorded but never remediated.

**Mitigation:** Dashboard should display health status prominently. Unhealthy subsystems should disable corresponding UI actions (e.g., gray out "Scan Patches" if Transport layer is unhealthy).

---

### RISK-05: Agent Update Trust Chain — ✅ RESOLVED

The `UpdateDirective` message includes `binary_sha256` and `signature_ed25519`, and `IntegrityChecker.cs` now implements both verification methods.

**Status:** Fixed — `IntegrityChecker.VerifyEd25519Async()` implemented using `NSec.Cryptography`. Agent reads the 32-byte Ed25519 public key from `AgentConfiguration.UpdateSigningPublicKey` (provisioned via config file, distributed alongside mTLS certs). `UpdateCommandHandler` verifies both SHA-256 hash and Ed25519 signature before staging updates. If no public key is configured, a warning is logged.

---

### RISK-06: No Idempotency Keys for Job Submission

`IJobScheduler.SubmitAsync(JobDefinition)` returns a `JobId` but the `JobDefinition` has no client-generated idempotency key. If the GUI submits a job and the response is lost (network error), the user may submit again, creating a duplicate job that patches the same machines twice.

**Impact:** Double patch application or double service restarts.

**Mitigation:** Add `Guid? IdempotencyKey` to `JobDefinition`. The orchestrator should reject duplicates within a time window.

---

### Revision 7 Additions — Async Command Pipeline

The following architectural enhancements were implemented in Revision 7 and are documented here for traceability.

#### ARCH-01: Async Fire-and-Forget Command Pipeline — ✅ IMPLEMENTED

**Components:**
- `ICommandBroker` ([src/HomeManagement.Abstractions/Interfaces/ICommandBroker.cs](src/HomeManagement.Abstractions/Interfaces/ICommandBroker.cs)) — interface exposing `SubmitAsync()` (returns tracking `Guid`) and `CompletedStream` (`IObservable<CommandCompletedEvent>`)
- `CommandBrokerService` ([src/HomeManagement.Transport/CommandBrokerService.cs](src/HomeManagement.Transport/CommandBrokerService.cs)) — singleton with bounded `Channel<QueuedCommand>` (capacity 256), background `ProcessLoopAsync`, scoped `IRemoteExecutor` per command, `IJobRepository` for result persistence, `Subject<CommandCompletedEvent>` for reactive updates
- `CommandEnvelope` / `CommandCompletedEvent` — data types for dispatch and result notification

**Integration points:**
- `ServicesViewModel` dispatches start/stop/restart via `_broker.SubmitAsync()` and subscribes to `CompletedStream` for auto-refresh
- `PatchingViewModel` subscribes to `CompletedStream` for async status feedback
- `JobExecutionQuartzJob` dispatches per-machine commands through `ICommandBroker`
- `App.axaml.cs` manages broker lifecycle: `_commandBroker.Start()` on startup, `DisposeAsync()` on shutdown
- Registered in DI via `TransportModuleRegistration` as singleton

#### ARCH-02: Agent Inbound Command Queue — ✅ IMPLEMENTED

**Location:** `src/HomeManagement.Agent/Communication/AgentHostService.cs`

Evolves CRIT-05 further. The gRPC receive loop enqueues into `Channel<CommandRequest>` (capacity 128, `BoundedChannelFullMode.Wait`). A dedicated `CommandProcessingLoopAsync` drains with `SemaphoreSlim`-bounded concurrency. This decouples stream reception from command execution, preventing slow commands from blocking the gRPC stream.

#### ARCH-03: Shell Command Handler — Windows Shell Change — ✅ IMPLEMENTED

**Location:** `src/HomeManagement.Agent/Handlers/ShellCommandHandler.cs`

Windows shell changed from `cmd.exe /C` to `powershell.exe -NoProfile -NonInteractive -Command`. All Windows strategies (service control, patch detection) generate PowerShell cmdlets that require a PS host. The previous `cmd.exe` invocation caused silent failures when executing PowerShell-specific commands.

#### ARCH-04: JobExecutionQuartzJob — Fully Wired — ✅ IMPLEMENTED

**Location:** `src/HomeManagement.Orchestration/JobSchedulerService.cs`

Previously a placeholder that immediately marked jobs as completed. Now deserializes `DefinitionJson` into `JobDefinitionData`, resolves machines via `IInventoryService`, builds OS-appropriate `RemoteCommand` per `JobType` (PatchScan, PatchApply, ServiceControl, MetadataRefresh), and dispatches through `ICommandBroker.SubmitAsync()`. `DefinitionJson` property added to `JobStatus` model and wired through `JobRepository` mapping.

#### ARCH-05: gRPC Port Change — 9443 → 9444 — ✅ IMPLEMENTED

Port changed to avoid conflict. Updated across all source files and all 14 architecture documents.

---

### Revision 8 Additions — Platform Architecture Implementation

The following platform services and infrastructure were implemented in Revision 8, mapping to doc 15 (Platform Architecture) and doc 16 (CI/CD Architecture).

#### PLAT-01: SQL Server Data Provider — ✅ IMPLEMENTED

**Location:** `src/HomeManagement.Data.SqlServer/`

`SqlServerServiceExtensions.AddHomeManagementSqlServer()` registers EF Core with SQL Server provider, retry policy (3 retries), command timeout (30s), and migrations assembly. `SqlServerDesignTimeDbContextFactory` enables `dotnet ef` tooling. Maps to doc 15 database migration requirement (SQLite → SQL Server).

#### PLAT-02: Authentication Service — ✅ IMPLEMENTED

**Location:** `src/HomeManagement.Auth/`, `src/HomeManagement.Auth.Host/`

**Auth library:** `JwtTokenService` (HMAC-SHA256 signing, configurable lifetime, refresh tokens), `LocalAuthProvider` (Argon2id hashing with 64 MB / 3 iterations / 4-parallelism), `ActiveDirectoryProvider` (LDAP bind), `RbacService` (4 roles: Viewer/Operator/Admin/Auditor, 15 permissions), `AuthServiceExtensions` (DI registration).

**Auth host:** ASP.NET Core Minimal API — `LoginEndpoints` (`/auth/login`), `TokenEndpoints` (`/auth/token`), `UserAdminEndpoints` (`/auth/user-admin/*`). JWT bearer auth, health checks at `/healthz` and `/readyz`.

Tests: 61 tests across 3 files (JwtTokenServiceTests, LocalAuthProviderTests, RbacServiceTests).

#### PLAT-03: Broker Microservice — ✅ IMPLEMENTED

**Location:** `src/HomeManagement.Broker.Host/`

ASP.NET Core REST API exposing all domain operations: machine management (`/api/machines`), patching (`/api/patching`), service control (`/api/services`), job scheduling (`/api/jobs`), credentials (`/api/credentials`), audit queries (`/api/audit`). JWT auth on all endpoints. SignalR hub at `/hubs/events` for real-time event streaming (job progress, agent status). References all domain modules and discovers `IModuleRegistration` implementations.

#### PLAT-04: Web GUI (Blazor Server) — ✅ IMPLEMENTED

**Location:** `src/HomeManagement.Web/`

Blazor Server with interactive server rendering. Refit-generated HTTP client for Broker API (`IBrokerApi`). Custom `AuthStateProvider` for cascading JWT-based auth. `EventHubClient` connects to Broker's SignalR hub. Pages: Dashboard, Login, Machines. Layout: MainLayout + NavMenu. Static assets served from wwwroot.

Tests: 12 tests in AuthStateProviderTests.

#### PLAT-05: Agent Gateway gRPC Server — ✅ IMPLEMENTED

**Location:** `src/HomeManagement.AgentGateway.Host/`

gRPC bidirectional streaming server on port 9444 using `agent_hub.proto`. `AgentGatewayGrpcService` handles agent handshake, heartbeat, command dispatch, and response collection. `AgentConnectionTracker` (ConcurrentDictionary) maintains in-memory agent state. Health checks at `/healthz` and `/readyz`.

Tests: 13 tests in AgentConnectionTrackerTests (including concurrent access).

#### PLAT-06: API Gateway (YARP) — ✅ IMPLEMENTED

**Location:** `src/HomeManagement.Gateway/`

YARP reverse proxy routing: `/api/*` → Broker service, `/auth/*` → Auth service. JWT validation via `HomeManagement.Auth`. Rate limiting and CORS policy configured. Route definitions in `yarp.json`.

#### PLAT-07: Container & Orchestration Infrastructure — ✅ IMPLEMENTED

**Dockerfiles (6):** `docker/` — Multi-stage builds (sdk:8.0 → aspnet:8.0) for agent, broker, auth, web, agent-gw, gateway.

**Kubernetes manifests (9):** `deploy/kubernetes/` — namespace, secrets, 5 deployments (broker with leader election, auth/web/gateway with HPA, agent-gw with leader election), services (ClusterIP + LoadBalancer), NGINX ingress (TLS termination, path-based routing).

**Docker Compose:** `deploy/docker/docker-compose.yaml` — Full local dev stack: SQL Server 2022 + all 5 services with health checks and dependency ordering.

#### PLAT-08: CI/CD Pipeline — ✅ IMPLEMENTED

Maps to doc 16 (CI/CD Architecture).

**CI workflow** (`.github/workflows/ci.yml`): Parallel jobs — build-and-test (restore, build, test with TRX upload, coverage collection) + code-quality (format check). Triggers on push/PR to main/develop. Concurrency: one run per ref.

**Release workflow** (`.github/workflows/release.yml`): Tag-triggered (v*). Four stages: validate → publish-binaries (win-x64, linux-x64) → docker-images (6 services to ghcr.io) → create-release (GitHub Release with artifacts).

**Dependabot** (`.github/dependabot.yml`): Weekly Monday updates with 7 grouped package categories.

**PR template** (`.github/pull_request_template.md`): Standard review checklist.

#### PLAT-09: GitHub Repository — ✅ CONNECTED

Private repository created via `gh repo create homeManagement --private --source=. --remote=origin --push`. All code, docs, CI/CD, and infrastructure pushed to GitHub.

---

## 7. Missing Components

### 7.1 Implementation Gaps — ✅ ALL MODULES IMPLEMENTED

All seven domain modules are now fully implemented:

| Module | Implementation | Status |
|---|---|---|
| **HomeManagement.Vault** | `CredentialVaultService` (AES-256-GCM, Argon2id KDF, idle auto-lock, reactive LockStateChanged), `VaultCrypto` | ✅ Complete |
| **HomeManagement.Transport** | `SshTransportProvider`, `WinRmTransportProvider`, `RemoteExecutorRouter`, `DefaultResiliencePipeline`, `RemotePathResolver` | ✅ Complete |
| **HomeManagement.Patching** | `PatchService`, `LinuxPatchStrategy`, `WindowsPatchStrategy` (JSON parsing) | ✅ Complete |
| **HomeManagement.Services** | `ServiceControllerService`, `LinuxServiceStrategy`, `WindowsServiceStrategy` (JSON parsing) | ✅ Complete |
| **HomeManagement.Inventory** | `InventoryService` (CRUD, CIDR discovery, CSV import/export) | ✅ Complete |
| **HomeManagement.Orchestration** | `JobSchedulerService` (Quartz.NET), `JobExecutionQuartzJob` | ✅ Complete |
| **HomeManagement.Auditing** | `AuditLoggerService` (HMAC-SHA256 chain), `SensitiveDataFilter` | ✅ Complete |

### 7.2 Infrastructure Gaps

| Component | Status | Impact |
|---|---|---|
| **Test projects** (unit + integration) | ✅ Created — 9 projects, 278 tests, 20 test files | Comprehensive regression safety covering domain + platform |
| **CI/CD pipeline** | ✅ Created — GitHub Actions CI + Release workflows, Dependabot | Automated build/test/format on every push; tag-triggered release with binaries + Docker |
| **EF Core migrations** | ✅ Generated — `20260316134330_Initial` (3 files) | Schema version-controlled |
| **Dockerfiles** | ✅ Created — 6 multi-stage Dockerfiles (agent, broker, auth, web, agent-gw, gateway) | All services containerized |
| **Kubernetes manifests** | ✅ Created — 9 manifests (namespace, secrets, 5 deployments, services, ingress) | Production K8s deployment ready |
| **Docker Compose** | ✅ Created — Full local dev stack (SQL Server 2022 + 5 services) | Local development orchestration |
| **GitHub repository** | ✅ Connected — Private repo with remote `origin` | Source control + CI/CD integration |
| **appsettings.json / configuration** | Not created | All config hardcoded |
| **CONTRIBUTING.md** | Not created | No developer onboarding guide |
| **View XAML files** (11 pages) | ✅ Created — All 11 pages with code-behind | Full UI page coverage |
| **GUI services** (DialogService, ClipboardService, IdleTimerService) | Not created | Missing user-facing features |
| **Reusable controls** (StatusBar, MachinePickerControl, etc.) | Not created | Missing shared UI components |
| **Async command pipeline** | ✅ Created — `ICommandBroker` + `CommandBrokerService` | Fire-and-forget dispatch, background execution, result persistence, CompletedStream |

### 7.3 Design Gaps in Existing Interfaces

| Gap | Location | Description | Status |
|---|---|---|---|
| ~~No `IObservable<bool> LockStateChanged`~~ | ~~`ICredentialVault`~~ | ~~Forces polling instead of reactive notification~~ | ✅ Fixed — `BehaviorSubject<bool>` in vault, subscribed in MainWindowViewModel |
| No `IdempotencyKey` | `JobDefinition` | Allows duplicate job submission | ✅ Fixed — `IdempotencyKey` on `JobDefinition`; `JobSchedulerService.SubmitAsync` checks via `GetByIdempotencyKeyAsync` |
| ~~No timeout parameter~~ | ~~`IResiliencePipeline.ExecuteAsync`~~ | ~~Callers cannot specify per-operation timeout~~ | ✅ Fixed — `ResilienceOptions.OperationTimeout` |
| ~~No `IUnitOfWork`~~ | ~~Repository layer~~ | ~~`SaveChangesAsync` on each repo creates ambiguity~~ | ✅ Fixed — `IUnitOfWork` extracted |
| No batch operations | `IMachineRepository` | Cannot bulk-delete or bulk-update efficiently | ✅ Fixed — `AddRangeAsync` + `SoftDeleteRangeAsync` on `IMachineRepository`; `BatchRemoveAsync` on `IInventoryService` |
| ~~No `protocol_version`~~ | ~~gRPC `Handshake`~~ | ~~No backward compatibility mechanism~~ | ✅ Fixed — `int32 protocol_version = 7` in proto |
| ~~No `correlation_id` echo~~ | ~~gRPC `CommandResponse`~~ | ~~Tracing broken at agent boundary~~ | ✅ Fixed — echoed in all dispatcher paths |

---

## 8. Assumption Register

The architecture makes the following unstated assumptions. Each should be explicitly documented and validated:

| # | Assumption | Risk If Wrong |
|---|---|---|
| A1 | The operator workstation has persistent network access to all managed machines | Offline machines accumulate stale state; jobs fail silently |
| A2 | SQLite write throughput is sufficient for the target fleet size | Write contention above ~100 machines |
| A3 | The user running the app has filesystem permissions for `vault.enc` and `homemanagement.db` | Startup crash with no helpful error |
| A4 | SSH.NET and WinRM libraries handle connection pooling internally | Without explicit pooling, TCP connection storms under load |
| A5 | All managed machines have a functional SSH or WinRM endpoint | Discovery returns machines that cannot be operated on |
| A6 | The system clock on operator and agent machines is synchronized (for audit timestamps and certificate validity) | Certificate validation failures, audit ordering issues |
| A7 | Agent certificates are pre-provisioned before first connection | No automated cert enrollment flow exists |
| A8 | Master password entropy is sufficient for Argon2id security | Weak passwords undermine the entire vault |
| A9 | A single user operates the system at a time | No concurrency control for multi-user scenarios |
| A10 | Remote commands produce UTF-8 output | Binary or non-UTF-8 output corrupts `RemoteResult.Stdout` |

---

## 9. Prioritized Remediation Plan

### Phase 0 — Critical Security Fixes ✅ COMPLETE

| Priority | Item | Finding | Effort | Status |
|---|---|---|---|---|
| P0-1 | Fix command injection in `ShellCommandHandler` | CRIT-01 | 2h | ✅ Done |
| P0-2 | Enable certificate revocation checking | CRIT-02 | 1h | ✅ Done |
| P0-3 | Add PFX password support to `CertificateLoader` | HIGH-12 | 1h | ✅ Done |

### Phase 1 — Critical Reliability Fixes ✅ COMPLETE

| Priority | Item | Finding | Effort | Status |
|---|---|---|---|---|
| P1-1 | Fix memory leaks in `JobsViewModel` and `CredentialsViewModel` | CRIT-03 | 1h | ✅ Done |
| P1-2 | Add thread safety and bound NavigationService back stack | CRIT-04 | 2h | ✅ Done |
| P1-3 | Await command dispatch in `AgentHostService` | CRIT-05 | 2h | ✅ Done |
| P1-4 | Forward `CancellationToken` in agent stream writes | CRIT-06 | 1h | ✅ Done |
| P1-5 | Add database initialization to `App.axaml.cs` | CRIT-07 | 1h | ✅ Done |
| P1-6 | Add startup error handling in `App.axaml.cs` | CRIT-08 | 1h | ✅ Done |
| P1-7 | Fix `GrpcChannelManager` disposed-channel race | CRIT-09 | 1h | ✅ Done |
| P1-8 | Protect audit events from deletion | CRIT-10 | 1h | ✅ Done |
| P1-9 | Add error handling to module discovery | CRIT-11 | 1h | ✅ Done |

### Phase 2 — High-Priority Design Fixes (Mostly Complete)

| Priority | Item | Finding | Effort | Status |
|---|---|---|---|---|
| P2-1 | Replace vault polling with reactive `LockStateChanged` | HIGH-01 | 2h | ✅ Done — `BehaviorSubject<bool>` in vault |
| P2-2 | Merge concurrent StatusMessage streams | HIGH-02 | 1h | ✅ Done |
| P2-3 | Implement `CalculateRunningJobs()` properly | HIGH-03 | 1h | ✅ Done (dead code removed) |
| P2-4 | Wire selected machines into PatchingViewModel job submission | HIGH-04 | 1h | ✅ Done |
| P2-5 | Store and dispose `ServiceProvider` on shutdown | HIGH-05 | 1h | ✅ Done |
| P2-6 | Implement graceful shutdown coordinator | HIGH-06 | 3h | ✅ Done |
| P2-7 | Add `protocol_version` to gRPC Handshake | HIGH-08 | 1h | ✅ Done |
| P2-8 | Add `correlation_id` to gRPC CommandResponse | HIGH-09 | 30m | ✅ Done |
| P2-9 | Enable SQLite WAL mode | MED-01 | 30m | ✅ Done |
| P2-10 | Wire `ISensitiveDataFilter` into Serilog pipeline | MED-06 | 2h | ✅ Done — `SensitivePropertyEnricher` |
| P2-11 | Harden `CommandValidator` regex with length guard | HIGH-07 | 1h | ✅ Done |
| P2-12 | Extract `IUnitOfWork` | HIGH-10 | 2h | ✅ Done |
| P2-13 | Add `ResilienceOptions` to `IResiliencePipeline` | HIGH-11 | 2h | ✅ Done |
| P2-14 | Normalize entity timestamp naming | MED-02 | 1h | ✅ Done — `AddedUtc` → `CreatedUtc` |
| P2-15 | Add FK constraint on `PatchHistoryEntity.JobId` | MED-03 | 30m | ✅ Done |

### Phase 3 — Module Implementation ✅ COMPLETE

All 7 domain modules implemented with full service classes, strategy patterns, and DI wiring:

| Order | Module | Status |
|---|---|---|
| 3.1 | Vault | ✅ Done — AES-256-GCM + Argon2id KDF |
| 3.2 | Transport | ✅ Done — SSH.NET + resilience pipeline |
| 3.3 | Auditing | ✅ Done — HMAC-SHA256 chain + sensitive data filter |
| 3.4 | Patching | ✅ Done — OS-specific strategies + history tracking |
| 3.5 | Services | ✅ Done — OS-specific strategies + snapshot recording |
| 3.6 | Inventory | ✅ Done — CRUD + CIDR discovery + CSV import/export |
| 3.7 | Orchestration | ✅ Done — Quartz.NET + progress streaming |

### Phase 4 — Quality & Deployment Infrastructure (Complete)

| Priority | Item | Effort | Status |
|---|---|---|---|
| P4-1 | Create unit test project with xUnit + NSubstitute | 1d | ✅ Done — 16 projects, 383 unit tests |
| P4-2 | Unit tests for each implemented module (target 80% coverage) | 5–10d | ✅ Done — 6 new test projects (Inventory, Patching, Services, Vault, Auditing, Orchestration) with 105 new tests |
| P4-3 | Integration test project with TestContainers for SQL Server | 2d | ✅ Done — SqlServerFixture + 20 tests (Machine, Job, Audit) |
| P4-4 | Generate initial EF Core migration (`dotnet ef migrations add Initial`) | 30m | ✅ Done — `20260316134330_Initial` |
| P4-5 | Create `appsettings.json` with externalized configuration | 1d | ✅ Done — 10 config files (5 base + 5 Development) across all platform services |
| P4-6 | CI pipeline (build + test on PR) | 1d | ✅ Done — GitHub Actions CI + Release workflows |
| P4-7 | Create View XAML files for all 11 pages | 5d | ✅ Done — All 11 views + code-behind |
| P4-8 | CONTRIBUTING.md and developer onboarding docs | 1d | ✅ Done — CONTRIBUTING.md with full developer guide |

---

## 10. Recommendations Summary

### Do Immediately — ✅ ALL COMPLETE
1. ~~**Fix the command injection vulnerability**~~ — CRIT-01 resolved
2. ~~**Fix all memory leaks**~~ — CRIT-03 resolved
3. ~~**Add startup error handling**~~ — CRIT-07, CRIT-08 resolved

### Do Before Module Implementation — ✅ ALL COMPLETE
4. ~~**Create the test project structure**~~ — ✅ Done (192 tests across 6 projects)
5. ~~**Generate the initial EF Core migration**~~ — ✅ Done (`20260316134330_Initial`)
6. ~~**Enable SQLite WAL mode**~~ — MED-01 resolved
7. ~~**Fix `PatchCommandHandler` injection**~~ — NEW-01 resolved (ArgumentList + input validation)

### Do During Module Implementation — ✅ COMPLETE
7. ~~**Implement `IUnitOfWork`**~~ — HIGH-10 resolved
8. **Add `IdempotencyKey` to `JobDefinition`** — prevents duplicate jobs (deferred to post-MVP)
9. ~~**Add `protocol_version` to gRPC Handshake**~~ — HIGH-08 resolved

### Defer to Post-MVP
10. Extract orchestrator/agent gateway to background service process
11. Add multi-user concurrency (row-level locking, conflict resolution)
12. Add automated certificate enrollment for agents
13. Add OCSP stapling for certificate validation
14. Evaluate PostgreSQL migration if fleet exceeds 200 machines

---

## 11. Re-Validation Summary

> **Revision 8 performed:** 2026-03-19
> **Methodology:** Full cross-reference audit of all 16 architecture docs vs. 28 projects. Validated Phase A platform implementation (doc 15) — 7 new service projects, 6 Dockerfiles, 9 K8s manifests, docker-compose. Validated CI/CD architecture (doc 16) — GitHub Actions CI + Release workflows, Dependabot. Verified 3 new test projects (Auth.Tests, AgentGateway.Host.Tests, Web.Tests) with 86 new tests covering JWT, RBAC, password hashing, agent connection tracking, Blazor auth state. GitHub repository connected (private). All original 47 findings unchanged.
> **Build status at time of validation:** 28/28 projects (19 src + 9 test), 0 errors, 0 warnings, 278 tests passing.

> **Revision 7 performed:** 2026-03-18
> **Methodology:** Full cross-reference audit of architecture docs vs. implementation. Validated async command pipeline (`ICommandBroker`, `CommandBrokerService`, agent inbound queue). Confirmed EF Core migration and all 11 View AXAML files exist. Updated CRIT-05 to reflect `Channel<CommandRequest>` evolution. Port 9443→9444 confirmed across all docs.
> **Build status at time of validation:** 18/18 projects, 0 errors, 0 warnings, 192 tests passing.

### Resolution Matrix

| Severity | Total | Resolved | Partial | Open |
|---|---|---|---|---|
| CRITICAL | 12 | **12** | 0 | 0 |
| HIGH | 14 | **14** | 0 | 0 |
| MEDIUM | 12 | **12** | 0 | 0 |
| LOW | 13 | **11** | 0 | 2 |
| **Total** | **51** | **49** | **0** | **2** |

### All Findings — Final Status

| ID | Severity | Description | Status |
|---|---|---|---|
| CRIT-01 | CRITICAL | Command injection in ShellCommandHandler | ✅ Resolved |
| CRIT-02 | CRITICAL | Certificate revocation disabled | ✅ Resolved |
| CRIT-03 | CRITICAL | Memory leak — nested subscriptions | ✅ Resolved |
| CRIT-04 | CRITICAL | NavigationService unbounded + thread-unsafe | ✅ Resolved |
| CRIT-05 | CRITICAL | Fire-and-forget command dispatch | ✅ Resolved |
| CRIT-06 | CRITICAL | CancellationToken bypassed in streaming | ✅ Resolved |
| CRIT-07 | CRITICAL | Database never initialized | ✅ Resolved |
| CRIT-08 | CRITICAL | No startup error handling | ✅ Resolved |
| CRIT-09 | CRITICAL | Disposed channel race | ✅ Resolved |
| CRIT-10 | CRITICAL | Missing audit immutability | ✅ Resolved |
| CRIT-11 | CRITICAL | Unhandled module exceptions | ✅ Resolved |
| HIGH-01 | HIGH | Vault polling → reactive notification | ✅ Resolved (Rev 6) |
| HIGH-02 | HIGH | Concurrent StatusMessage writes | ✅ Resolved |
| HIGH-03 | HIGH | Dead code — RunningJobCount | ✅ Resolved |
| HIGH-04 | HIGH | PatchingViewModel empty job | ✅ Resolved |
| HIGH-05 | HIGH | No ServiceProvider disposal | ✅ Resolved |
| HIGH-06 | HIGH | No graceful shutdown | ✅ Resolved |
| HIGH-07 | HIGH | Regex timeout in CommandValidator | ✅ Resolved |
| HIGH-08 | HIGH | No protocol_version in Handshake | ✅ Resolved |
| HIGH-09 | HIGH | No correlation_id echo | ✅ Resolved |
| HIGH-10 | HIGH | Repository SaveChanges per repo | ✅ Resolved |
| HIGH-11 | HIGH | IResiliencePipeline underspecified | ✅ Resolved |
| HIGH-12 | HIGH | No PFX password support | ✅ Resolved |
| MED-01 | MEDIUM | Missing WAL mode | ✅ Resolved |
| MED-02 | MEDIUM | Inconsistent entity timestamps | ✅ Resolved |
| MED-03 | MEDIUM | PatchHistory FK missing | ✅ Resolved |
| MED-04 | MEDIUM | Log directory not created | ✅ Resolved |
| MED-05 | MEDIUM | Log retention disk space | ✅ Resolved (Rev 6) |
| MED-06 | MEDIUM | Sensitive data filter not wired | ✅ Resolved |
| MED-07 | MEDIUM | ReconnectPolicy counter bug | ✅ Resolved |
| MED-08 | MEDIUM | Output truncation no indicator | ✅ Resolved |
| MED-09 | MEDIUM | RunSafe catches OOM | ✅ Resolved |
| NEW-01 | HIGH | PatchCommandHandler injection | ✅ Resolved |
| NEW-02 | MEDIUM | ServiceCommandHandler arguments | ✅ Resolved |
| NEW-03 | MEDIUM | PatchApply routing gap | ✅ Resolved |
| NEW-04 | LOW | Double timeout CTS | ✅ Resolved (Rev 6) |
| NEW-05 | LOW | ReconnectPolicy not thread-safe | ✅ Resolved |
| NEW-06 | LOW | Undisposed subscriptions in VMs | ✅ Resolved |
| NEW-07 | LOW | TimeAgoConverter timezone | ✅ Resolved (Rev 6) |
| NEW-08 | LOW | NavigationService O(n) eviction | ✅ Resolved (Rev 6) |
| SEC-01 | CRITICAL | gRPC agent spoofing — no auth | ✅ Resolved (Rev 11) |
| SEC-02 | MEDIUM | SensitivePropertyEnricher missing in Gateway/AgentGW | ✅ Resolved (Rev 11) |
| SEC-03 | HIGH | JWT placeholder key accepted silently | ✅ Resolved (Rev 11) |
| SEC-04 | LOW | Production config defaults (deploy checklist) | ⚠️ Open (by design) |

**2 remaining LOW (open):** SEC-04 deployment defaults are environment configuration, not code defects. One prior LOW deferred to Phase 4.

### Verified Fixes by File (cumulative through Rev 11)

| File | Findings Addressed |
|---|---|
| `ShellCommandHandler.cs` | CRIT-01, NEW-04 (timeout centralized), ARCH-03 (cmd.exe→powershell.exe) |
| `PatchCommandHandler.cs` | NEW-01 (ArgumentList + SafePatchIdPattern) |
| `PatchApplyCommandHandler.cs` | NEW-03 (routing gap) |
| `ServiceCommandHandler.cs` | NEW-02 (ArgumentList migration) |
| `CommandValidator.cs` | MED-07, HIGH-07 (length guard + timeout catch) |
| `CommandDispatcher.cs` | HIGH-09 (correlation_id echo) |
| `ReconnectPolicy.cs` | MED-07, TimeSpan overflow bug, NEW-05 (Interlocked) |
| `CertificateLoader.cs` | CRIT-02, HIGH-12 |
| `AgentConfiguration.cs` | HIGH-12, RISK-05 (UpdateSigningPublicKey) |
| `AgentHostService.cs` | CRIT-05, CRIT-06, HIGH-06, ARCH-02 (inbound Channel queue) |
| `GrpcChannelManager.cs` | CRIT-09 |
| `IntegrityChecker.cs` | RISK-05 (Ed25519 verification via NSec) |
| `UpdateCommandHandler.cs` | RISK-05 (Ed25519 + SHA-256 verification) |
| `Program.cs` (Agent) | NEW-03, RISK-05 (handler/config registration) |
| `ICredentialVault.cs` | HIGH-01 (LockStateChanged observable) |
| `CredentialVaultService.cs` | HIGH-01 (BehaviorSubject<bool>), idle auto-lock |
| `MainWindowViewModel.cs` | HIGH-01 (reactive subscription replaces polling), HIGH-02, HIGH-03 |
| `JobsViewModel.cs` | CRIT-03 |
| `CredentialsViewModel.cs` | CRIT-03 |
| `MachinesViewModel.cs` | NEW-06 (DisposeWith) |
| `DashboardViewModel.cs` | NEW-06 (DisposeWith) |
| `AuditLogViewModel.cs` | NEW-06 (DisposeWith) |
| `NavigationService.cs` | CRIT-04, NEW-08 (while→if) |
| `TimeAgoConverter.cs` | NEW-07 (UTC normalization) |
| `App.axaml.cs` | CRIT-07, CRIT-08, HIGH-05, ARCH-01 (CommandBrokerService lifecycle) |
| `PatchingViewModel.cs` | HIGH-04, ARCH-01 (ICommandBroker + CompletedStream subscription) |
| `ServicesViewModel.cs` | ARCH-01 (ICommandBroker dispatch + CompletedStream auto-refresh) |
| `ViewModelBase.cs` | MED-09 |
| `HomeManagementDbContext.cs` | CRIT-10, MED-03 (PatchHistory FK) |
| `Entities.cs` | MED-02 (AddedUtc→CreatedUtc), MED-03 (Job navigation) |
| `ServiceRegistration.cs` | CRIT-11, MED-01, MED-04, MED-05 (disk space guard), HIGH-10 |
| `SensitivePropertyEnricher.cs` | MED-06 (new file), SEC-02 (internal→public for cross-project use) |
| `SystemHealthService.cs` | Health aggregation (new file) |
| `DomainRepositories.cs` | HIGH-10 (IUnitOfWork interface) |
| `UnitOfWork.cs` | HIGH-10 (new file) |
| `IResiliencePipeline.cs` | HIGH-11 (ResilienceOptions record) |
| `DefaultResiliencePipeline.cs` | HIGH-11 (IOptions<ResilienceOptions>) |
| `TransportModuleRegistration.cs` | HIGH-11 (options registration), ARCH-01 (CommandBrokerService DI) |
| `WinRmTransportProvider.cs` | Transport gap (new file) |
| `RemoteExecutorRouter.cs` | WinRM routing |
| `MachineRepository.cs` | MED-02 (CreatedUtc rename) |
| `InventoryService.cs` | MED-02 (CreatedUtc rename) |
| `InventoryModels.cs` | MED-02 (CreatedUtc rename) |
| `ICommandBroker.cs` | ARCH-01 (new file — interface + CommandEnvelope + CommandCompletedEvent) |
| `CommandBrokerService.cs` | ARCH-01 (new file — Channel<T> queue, background loop, persistence) |
| `JobSchedulerService.cs` | ARCH-04 (JobExecutionQuartzJob fully wired, DefinitionJson serialization) |
| `JobModels.cs` | ARCH-04 (DefinitionJson property added to JobStatus) |
| `JobRepository.cs` | ARCH-04 (DefinitionJson mapping) |
| `JobsView.axaml` | ARCH-04 (CreatedUtc→SubmittedUtc binding fix) |
| `PatchingView.axaml` | ARCH-01 (machine ComboBox selector, StatusMessage display) |
| `ServicesView.axaml` | ARCH-01 (State binding fix, StatusMessage display) |

### Security Fixes (Rev 11)

| File | Finding Addressed |
|---|---|
| `ApiKeyInterceptor.cs` (new) | SEC-01 — gRPC pre-shared key authentication on all methods |
| `AgentGateway.Host/Program.cs` | SEC-01 (interceptor registration), SEC-02 (SensitivePropertyEnricher) |
| `AgentGateway.Host/appsettings.json` | SEC-01 (AgentGateway:ApiKey config) |
| `Gateway/Program.cs` | SEC-02 (SensitivePropertyEnricher enricher added) |
| `Gateway.csproj` | SEC-02 (HomeManagement.Core reference) |
| `JwtTokenService.cs` | SEC-03 (startup guard rejects CHANGE-ME placeholder keys) |

### New Platform Files (Rev 8)

| File/Project | Architecture Mapping |
|---|---|
| `HomeManagement.Data.SqlServer` | PLAT-01 — SQL Server EF Core provider (doc 15 database migration) |
| `HomeManagement.Auth` | PLAT-02 — JWT, Argon2id local auth, LDAP/AD, RBAC (doc 15 enterprise auth) |
| `HomeManagement.Auth.Host` | PLAT-02 — Identity service REST API (doc 15 auth service) |
| `HomeManagement.Broker.Host` | PLAT-03 — Domain REST API + SignalR hub (doc 15 broker microservice) |
| `HomeManagement.Web` | PLAT-04 — Blazor Server GUI, Refit client, SignalR (doc 15 web GUI) |
| `HomeManagement.AgentGateway.Host` | PLAT-05 — gRPC server, connection tracking (doc 15 agent gateway) |
| `HomeManagement.Gateway` | PLAT-06 — YARP reverse proxy, JWT middleware (doc 15 API gateway) |
| `docker/*.Dockerfile` (6 files) | PLAT-07 — Container images for all services (doc 15 containerization) |
| `deploy/kubernetes/*.yaml` (9 files) | PLAT-07 — K8s deployment, services, ingress (doc 15 orchestration) |
| `deploy/docker/docker-compose.yaml` | PLAT-07 — Local dev stack (doc 15 development workflow) |
| `.github/workflows/ci.yml` | PLAT-08 — CI pipeline (doc 16 CI/CD) |
| `.github/workflows/release.yml` | PLAT-08 — Release pipeline (doc 16 CI/CD) |
| `.github/dependabot.yml` | PLAT-08 — Dependency automation (doc 16 CI/CD) |
| `.github/pull_request_template.md` | PLAT-08 — PR review process (doc 16 CI/CD) |
| `HomeManagement.Auth.Tests` (3 files) | PLAT-02 — 61 tests for JWT, password hashing, RBAC |
| `HomeManagement.AgentGateway.Host.Tests` (1 file) | PLAT-05 — 13 tests for connection tracking |
| `HomeManagement.Web.Tests` (1 file) | PLAT-04 — 12 tests for Blazor auth state |

### Test Coverage Summary

| Test Project | Tests | What's Covered |
|---|---|---|
| **Abstractions.Tests** | 42 | `Hostname`, `ServiceName`, `CidrRange` validated types; `CorrelationContext` async scoping |
| **Agent.Tests** | 83 | `CommandValidator` (allowlist, elevation, rate limiting, length guard); `CommandDispatcher` (routing, rejection, exception, timeout, correlation); `ReconnectPolicy` (backoff, cap, reset); `IntegrityChecker` (SHA-256); `PatchCommandHandler`; `ServiceCommandHandler`; `PatchApplyCommandHandler` |
| **Core.Tests** | 21 | `SensitivePropertyEnricher` (redaction patterns, non-sensitive passthrough, mixed properties, edge cases) |
| **Data.Tests** | 11 | `HomeManagementDbContext` audit immutability; `UnitOfWork` (constructor, save, dispose) |
| **Gui.Tests** | 24 | `NavigationService` (navigation, bounds, disposal, concurrency); `TimeAgoConverter`; `BoolToVisibilityConverter` |
| **Transport.Tests** | 11 | `DefaultResiliencePipeline` (retry, circuit breaker, timeout, per-target isolation, cancellation) |
| **Auth.Tests** | 61 | `JwtTokenService` (generation, claims, expiry, validation, tampered/wrong-key/expired, refresh tokens, validation params); `LocalAuthProvider` (Argon2id hash format, salt/hash base64, unique salts, verify correct/wrong/malformed, case sensitivity, special chars); `RbacService` (HasPermission per role/multi-role/case-insensitive, GetEffectivePermissions dedup/empty, GetDefaultRoles names/IDs/permissions) |
| **AgentGateway.Host.Tests** | 13 | `AgentConnectionTracker` (register new/update/multiple, unregister existing/nonexistent/double, get existing/missing, getAll empty/populated, count lifecycle, concurrent access) |
| **Web.Tests** | 12 | `AuthStateProvider` (initial unauthenticated, SetAuthenticatedUser with roles/no-roles/override, ClearAuthenticationState, notification events) |
| **Inventory.Tests** | 11 | `InventoryService` CRUD, batch remove, metadata refresh, export JSON/CSV |
| **Patching.Tests** | 22 | `PatchService` (detect, apply, history, installed); `LinuxPatchStrategy` (commands, parse, sanitize); `WindowsPatchStrategy` (parse JSON, sanitize KB IDs) |
| **Services.Tests** | 15 | `LinuxServiceStrategy` (systemctl commands, parse status/list, control verbs); `WindowsServiceStrategy` (JSON parse, list, control, numeric enums) |
| **Vault.Tests** | 14 | `CredentialVaultService` lock/unlock lifecycle, LockStateChanged events, add/get/update/remove credentials, encryption roundtrip |
| **Auditing.Tests** | 10 | `AuditLoggerService` persistence, redaction, correlation ID, chain hash computation, query, count, export |
| **Orchestration.Tests** | 9 | `JobSchedulerService` submit/idempotency/status/list/cancel, progress stream, dispose |
| **Integration.Tests** | 20 | `MachineRepository` (roundtrip, soft delete, batch, query, pagination); `JobRepository` (roundtrip, idempotency, results, query, update); `AuditImmutability` (add, modify/delete throws, soft-delete filter) — requires Docker |

**Total: 383 unit tests across 15 projects + 20 integration tests (16 test projects, 27 test files).**

### Remaining Infrastructure Items (Post-MVP)

| Item | Priority | Effort |
|---|---|---|
| ~~Generate EF Core migration~~ | ~~P4~~ | ~~30m~~ ✅ Done |
| ~~Create `appsettings.json`~~ | ~~P4~~ | ~~1d~~ ✅ Done (Rev 10) |
| ~~CI/CD pipeline~~ | ~~P4~~ | ~~1d~~ ✅ Done |
| ~~View XAML files (11 pages)~~ | ~~P4~~ | ~~5d~~ ✅ Done |
| ~~Integration test project (TestContainers for SQL Server)~~ | ~~P4~~ | ~~2d~~ ✅ Done (Rev 10) |
| ~~Unit test coverage → 80%~~ | ~~P4~~ | ~~5-10d~~ ✅ Done (Rev 10) — 383 unit tests |
| ~~CONTRIBUTING.md~~ | ~~P4~~ | ~~1d~~ ✅ Done (Rev 10) |
| ~~DialogService, ClipboardService, IdleTimerService~~ | ~~P3~~ | ~~1d~~ ✅ Done |
| ~~Reusable controls (StatusBar, MachinePickerControl, etc.)~~ | ~~P3~~ | ~~3-5d~~ ✅ Done |
| ~~`IdempotencyKey` on JobDefinition~~ | ~~P3~~ | ~~2h~~ ✅ Done |
| ~~Batch operations on IMachineRepository~~ | ~~P3~~ | ~~3h~~ ✅ Done |
| SQL Server EF Core migration for platform schema | P3 | 2h |
| SAML/OAuth providers in Auth service | P3 | 3-5d |
| ~~Helm chart for Kubernetes deployments~~ | ~~P3~~ | ~~2d~~ ✅ Done |
| ~~Centralized logging (Loki/Seq) integration~~ | ~~P3~~ | ~~1d~~ ✅ Done |
| ~~Prometheus metrics endpoints~~ | ~~P3~~ | ~~1d~~ ✅ Done |
