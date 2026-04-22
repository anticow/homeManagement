# 17 — Remediation And Decision Tracker

> **Status: CLOSED** — All 4 architecture decisions resolved as of 2026-04-20. Retained for historical reference.
>
> Tracking document for open findings from the 2026-03-21 independent code, architecture, and deployment review.
>
> Purpose:
> - keep remediation status visible
> - make architecture decisions explicit
> - tie implementation work back to a concrete finding
> - avoid “fixed in chat” without durable repository traceability

---

## 1. Status Model

| Status | Meaning |
|---|---|
| OPEN | Finding confirmed, not yet assigned to an implementation approach |
| DECISION-NEEDED | Work is blocked on an architecture decision |
| IN-PROGRESS | Design chosen and implementation underway |
| BLOCKED | Work cannot continue because of an external dependency or unresolved prerequisite |
| RESOLVED | Code, tests, docs, and deployment assets updated; closure criteria met |
| ACCEPTED-RISK | Team explicitly accepts the remaining risk with rationale |

---

## 2. Architecture Decisions Queue

| Decision ID | Topic | Required Decision | Options | Current Status | Exit Criteria |
|---|---|---|---|---|---|
| ADR-R12-01 | Agent control plane | Choose the single authoritative control-plane architecture. | A: Keep desktop-hosted gRPC control plane and remove standalone AgentGateway. B: Complete standalone AgentGateway and retire desktop-hosted path. C: Keep both temporarily with an explicit compatibility bridge and deprecation plan. | RESOLVED - Option B | Docs 11/13/15 updated, Broker owns command state, AgentGateway owns agent streams only, desktop-hosted control plane marked deprecated or removed |
| ADR-R12-02 | Authentication architecture | Decide whether Auth.Host is production-path code or scaffolding. | A: Complete Auth.Host as the system of record. B: Mark Auth.Host non-production and remove it from deploy path until complete. | RESOLVED - Option A | Auth design documented, deploy path updated, placeholder endpoints removed or completed |
| ADR-R12-03 | Web auth session model | Decide how Web obtains, stores, refreshes, and forwards tokens. | A: JWT-based browser/session model. B: BFF or server-side session model. | RESOLVED - Option B | Login/logout/refresh flow documented and implemented end to end |
| ADR-R12-04 | Deployment support boundary | Decide whether raw Kubernetes manifests and Helm are both supported production artifacts. | A: Helm only. B: Raw manifests and Helm both supported. | RESOLVED - Option A | One supported path documented, the other either aligned or marked dev-only |

---

## 2.1 Remaining Three-Decision Correction Plan

This section is the active execution plan for resolving the three remaining architecture decisions and moving immediately into correction work.

### Program Completion Checklist

- [x] ADR-R12-02 resolved and correction work completed
- [x] ADR-R12-03 resolved and correction work completed
- [x] ADR-R12-04 resolved and correction work completed

### Sequencing Rule

Work the remaining decisions in this order:

1. Authentication architecture
2. Web session model
3. Deployment support boundary

That order is intentional. Web authentication depends on the Auth boundary, and deployment support should not be finalized until the runtime shape is clear.

### Track A — ADR-R12-02 Authentication Architecture

**Resolved target:** Option A — `Auth.Host` is the system of record.

**Reasoning**

- the chosen control-plane model already assumes a platform Auth boundary
- Web, Gateway, and Broker all need one authoritative issuer and policy source
- backing away from Auth.Host now would increase temporary coupling and rework

**Decision checklist**

- [x] confirm `Auth.Host` is the production-path issuer for access and refresh tokens
- [x] define the first supported provider set for correction release: Local auth required, enterprise providers optional behind feature flags
- [x] define the persisted model for users, roles, sessions, and refresh token revocation
- [x] define the authorization policy surface used by Broker and admin endpoints
- [x] update docs 05, 13, and 15 with the resolved Auth architecture
- [x] change ADR-R12-02 status to `RESOLVED`

**Correction checklist**

- [x] implement login, refresh, revoke, and admin endpoints without placeholder responses
- [x] add authentication and authorization middleware to `Auth.Host`
- [x] add persistence for users, roles, refresh tokens, and revocation state
- [x] enforce admin authorization on user and role management endpoints
- [x] add unit and integration tests for token issuance, refresh, revoke, and admin authorization
- [x] update tracker item `REV12-02` to `RESOLVED`

### Track B — ADR-R12-03 Web Auth Session Model

**Recommended target:** Option B — BFF/server-side session model.

**Reasoning**

- the current Web client is Blazor Server, so a server-side session boundary is simpler and safer than browser-managed bearer tokens
- it reduces token leakage risk and keeps auth flow understandable
- it aligns with the decision that Web is a client, not a control-plane component

**Decision checklist**

- [x] confirm `hm-web` uses a server-side authenticated session and does not treat client-local role state as authoritative
- [x] define how `hm-web` exchanges credentials with `hm-auth`
- [x] define how `hm-web` stores, refreshes, and invalidates access on behalf of the user
- [x] define how Broker API calls are authenticated from the web tier
- [x] update docs 13 and 15 with the resolved Web session model
- [x] change ADR-R12-03 status to `RESOLVED`

**Correction checklist**

- [x] remove placeholder local login behavior from `Login.razor` and the current `AuthStateProvider` shortcut flow
- [x] implement real sign-in, sign-out, and expired-session handling in `hm-web`
- [x] ensure server-side state, not UI-local role assignment, drives authorization-sensitive rendering
- [x] add authenticated Broker API calling path from `hm-web`
- [x] add focused web/session tests for anonymous state, authenticated access, and session expiry behavior
- [x] update tracker item `REV12-03` to `RESOLVED`

### Track C — ADR-R12-04 Deployment Support Boundary

**Recommended target:** Option A — Helm is the supported production artifact; raw Kubernetes manifests become dev/reference only unless later brought to parity.

**Reasoning**

- one authoritative deployment path reduces operational drift
- current raw manifests and Helm templates are already diverging
- the fastest path to a reliable correction is to pick one supported artifact and validate it aggressively

**Decision checklist**

- [x] confirm Helm is the supported production deployment artifact
- [x] classify raw manifests as dev/reference-only or remove them after equivalent scripted generation exists
- [x] define the minimum CI validation expected for the supported deployment path
- [x] update docs 05, 15, and 16 with the supported deployment boundary
- [x] change ADR-R12-04 status to `RESOLVED`

**Correction checklist**

- [x] fix `hm-agent-gw` Helm/runtime wiring: probes, required API key secret, and environment variables
- [x] remove ambiguous dev-only defaults from the supported deployment path
- [x] make ingress and TLS behavior internally consistent
- [x] add chart render/lint/validation checks to CI
- [x] mark raw manifests clearly as dev/reference-only if they remain in the repo
- [x] update tracker items `REV12-04` and `REV12-06` to `RESOLVED`

### Cross-Track Correction Follow-Through

After the three decisions are resolved and the correction checklists are complete, finish the remaining technical clean-up tied to the chosen architecture:

- [x] complete Broker <-> AgentGateway command and result flow for `REV12-01`
- [x] remove desktop-hosted production control-plane dependencies from the supported runtime path
- [x] align agent command concurrency behavior or configuration surface for `REV12-05`
- [x] add end-to-end validation covering Web -> Broker -> AgentGateway -> Agent
- [x] update `14-ARCHITECTURE-VALIDATION.md` with a closure revision summarizing completed work

---

## 3. Findings Backlog

| ID | Severity | Area | Status | Depends On | Summary |
|---|---|---|---|---|---|
| REV12-01 | CRITICAL | Agent Control Plane | RESOLVED | ADR-R12-01 | Broker and GUI now use the standalone AgentGateway via the remote `IAgentGateway` client, agent sessions authenticate with API-key metadata, command results complete inside the standalone host, and the desktop runtime no longer starts the embedded control server. |
| REV12-02 | HIGH | Auth | RESOLVED | ADR-R12-02 | Auth.Host is now the system-of-record authentication boundary with persisted users, roles, refresh tokens, bootstrap admin seeding, protected admin endpoints, and passing unit plus integration tests. |
| REV12-03 | HIGH | Web/Auth Integration | RESOLVED | ADR-R12-02, ADR-R12-03 | `hm-web` now uses a server-side session backed by `Auth.Host`, forwards bearer tokens to Broker from the server tier, and clears session state on refresh failure or logout. |
| REV12-04 | HIGH | Deployment | RESOLVED | ADR-R12-01, ADR-R12-04 | The supported Helm chart now injects the required `AgentGateway__ApiKey`, exposes `hm-agent-gw` on the correct gRPC service type, and aligns gateway and web runtime configuration with the current platform hosts. |
| REV12-05 | MEDIUM | Reliability/Performance | RESOLVED | ADR-R12-01 | Agent command processing now honors `MaxConcurrentCommands` through a bounded execution service with focused concurrency tests. |
| REV12-06 | MEDIUM | Deployment/Security | RESOLVED | ADR-R12-04 | The supported deployment path is now Helm-only, ingress SSL redirect is explicit instead of contradictory, CI renders and lints the chart, raw manifests are marked reference-only, and chart rendering fails fast when required secret values are omitted. |

---

## 4. Detailed Tracking Items

### ADR-R12-01 — Resolved Architecture Decision

**Decision:** Option B — complete the standalone AgentGateway and retire the desktop-hosted control plane.

**Chosen target model**

- `hm-broker` is the authoritative control-plane core
- `hm-agent-gw` is the only supported agent ingress and owns long-lived bidirectional agent streams
- `hm-broker` owns command intent, scheduling, persistence, timeout policy, and result state
- `hm-agent-gw` does not implement domain workflow logic; it forwards command traffic and agent events between agents and Broker
- Web and Desktop are both clients of Broker and Auth; neither is part of the supported control-plane runtime

**Why this model**

- simpler responsibility split than dual control planes
- more reliable than embedding agent connectivity inside the desktop process
- easier to scale and reason about than pushing orchestration state into the stream gateway
- expandable because agent transport can evolve without moving job, audit, or scheduling ownership out of Broker

**Segmentation rule**

Control-plane responsibilities are divided into three layers only:

1. Client layer: Web UI and Desktop UI submit intent and display state.
2. Control core: Broker validates intent, schedules work, persists state, audits actions, and exposes APIs.
3. Agent edge: AgentGateway maintains agent sessions and relays commands/results/events.

**Explicit non-goals**

- no desktop-hosted production gRPC server
- no duplicate command orchestration in AgentGateway
- no agent-originated direct writes into domain persistence

**Migration consequence**

`GrpcServerHost` and the desktop-hosted `AgentHubService` path are transitional compatibility code only and should be removed after Broker-to-AgentGateway command flow is complete.

### REV12-01 — Agent Control Plane Split-Brain

**Severity:** CRITICAL  
**Status:** RESOLVED  
**Depends On:** ADR-R12-01

**Problem**

The repository previously contained two different control-plane models:

- standalone AgentGateway host with API-key-gated gRPC ingress
- legacy desktop-hosted gRPC server backed by `AgentGatewayService`

The newer path now owns the supported command-response lifecycle, and the older desktop-hosted server is no longer started on the supported runtime path.

**Evidence**

- `src/HomeManagement.AgentGateway.Host/Services/ApiKeyInterceptor.cs` requires `x-agent-api-key` and the agent now sends it during `Connect(...)`
- `src/HomeManagement.AgentGateway.Host/Services/StandaloneAgentGatewayService.cs` owns session registration, outbound command dispatch, and command completion
- `src/HomeManagement.AgentGateway.Host/Services/AgentGatewayGrpcService.cs` now completes pending commands through the standalone host service instead of logging a TODO
- `src/HomeManagement.Transport/RemoteAgentGatewayClient.cs` is now the operative `IAgentGateway` implementation used by Broker and GUI
- `src/HomeManagement.Gui/App.axaml.cs` no longer starts the embedded `GrpcServerHost`

**Resolved Design Decision**

The authoritative control plane is:

- Broker as system of record for command intent and execution state
- standalone AgentGateway as the only supported agent ingress
- Desktop and Web as clients only

The desktop-hosted control-plane path is transitional and must be removed after parity is reached.

**Implementation Goals**

- align Agent, AgentGateway, Broker, and Transport integration around the single supported command path
- ensure auth requirements are satisfied by the agent client
- ensure Broker owns command submission, completion, timeout, disconnect handling, and persistence
- ensure AgentGateway is limited to connection lifecycle, relaying, and agent-presence events
- ensure desktop uses Broker APIs instead of hosting its own agent server

**Closure Criteria**

- authoritative control-plane path is the only supported runtime path
- agent handshake now satisfies standalone gateway API-key requirements
- standalone AgentGateway completes command request/response lifecycle for Broker and GUI callers
- deprecated desktop-hosted control path is no longer part of the supported runtime startup path

**Completion Notes**

- standalone AgentGateway now owns agent session registration, pending command completion, metadata access, and outbound control messages
- Broker and GUI use the remote `IAgentGateway` client instead of the embedded in-process gateway service
- the desktop application no longer starts the embedded gRPC control-plane host at startup
- agent gRPC connections now include the configured API-key metadata expected by the standalone gateway
- the desktop runtime now supports a platform mode that authenticates against `Auth.Host` and resolves inventory, patching, jobs, services, audit, credentials, and connection testing through explicit Broker/Auth clients
- Broker-hosted job execution now starts Quartz and the async command broker loop automatically as host-native services instead of relying on desktop startup
- focused end-to-end coverage now verifies authenticated Broker job submission through AgentGateway to Agent with persisted job completion state

### REV12-02 — Auth Service Exposed Before Completion

**Severity:** HIGH  
**Status:** RESOLVED  
**Depends On:** ADR-R12-02

**Problem**

`Auth.Host` is deployed as a runtime service, but login, refresh, revoke, and user administration remain scaffolded.

**Evidence**

- `src/HomeManagement.Auth.Host/Endpoints/LoginEndpoints.cs` returns a placeholder response
- `src/HomeManagement.Auth.Host/Endpoints/TokenEndpoints.cs` contains TODO-based refresh and revoke handlers
- `src/HomeManagement.Auth.Host/Endpoints/UserAdminEndpoints.cs` maps admin endpoints without explicit authorization requirements
- `src/HomeManagement.Auth.Host/Program.cs` exposes these endpoints directly

**Resolved Design Decision**

`Auth.Host` is part of the supported production runtime and is the system of record for platform authentication.

For the current correction release:

- Local authentication is required and supported
- enterprise providers remain optional and can be added behind configuration later
- Auth.Host owns access token issuance, refresh token rotation, revocation, bootstrap admin seeding, and admin user management

**Implementation Goals**

- implement user store, password verification, refresh-token persistence, revoke semantics, and endpoint authorization
- make admin access require authenticated `Admin` role membership
- provide a first-run bootstrap path without leaving placeholder runtime behavior in place

**Closure Criteria**

- no placeholder auth endpoints remain on production path
- authorization rules are explicit and tested
- docs 05, 13, and 15 reflect the actual authentication model

**Completion Notes**

- persisted auth tables now back users, roles, user-role assignment, and refresh token revocation
- `Auth.Host` now enables authentication and authorization middleware
- login, refresh, revoke, list users, create user, assign roles, and list roles are implemented
- focused unit and integration tests are passing for the corrected Auth flow

### REV12-03 — Web Authentication Is Placeholder-Only

**Severity:** HIGH  
**Status:** DECISION-NEEDED  
**Depends On:** ADR-R12-02, ADR-R12-03

**Problem**

The Web UI locally marks the user authenticated and assigns the `Operator` role without validating credentials or storing a real token, while Broker APIs require actual authorization.

**Evidence**

- `src/HomeManagement.Web/Components/Pages/Login.razor` contains a TODO for Auth API integration and calls `SetAuthenticatedUser(Username, ["Operator"])`
- `src/HomeManagement.Web/Program.cs` configures a plain Refit client and local auth state provider
- `src/HomeManagement.Broker.Host/Endpoints/MachineEndpoints.cs` requires authorization, representative of the secured Broker API surface

**Required Design Decision**

Define the supported Web authentication/session model and align the implementation to it.

**Implementation Goals**

- implement real login, logout, refresh, and token forwarding
- ensure client-visible role state matches backend authorization state
- add integration tests for authenticated and unauthenticated broker access from Web

**Closure Criteria**

- login path uses the supported Auth architecture
- Web API calls carry valid credentials
- role-based UI behavior is derived from server-valid state

### REV12-04 — AgentGateway Deployment Is Not Viable As Written

**Severity:** HIGH  
**Status:** RESOLVED  
**Depends On:** ADR-R12-01, ADR-R12-04

**Problem**

The AgentGateway deploy assets do not match the host’s runtime requirements.

**Evidence**

- `deploy/kubernetes/agent-gw-deployment.yaml` exposes port 9444 but probes port 8080
- `src/HomeManagement.AgentGateway.Host/Services/ApiKeyInterceptor.cs` requires `AgentGateway:ApiKey`
- raw manifests do not provide `AgentGateway__ApiKey`
- `deploy/helm/homemanagement/templates/agent-gw-deployment.yaml` injects an unrelated connection string but not the required API key

**Implementation Goals**

- align ports, probes, and environment variables with runtime behavior
- provide required secrets/config for the supported deployment path
- add deployment validation to CI or release checks

**Closure Criteria**

- supported AgentGateway deployment starts healthy in the chosen deploy path
- required configuration keys are provisioned from secrets/config
- smoke test proves agent connection against deployed gateway

### REV12-05 — Agent Command Processing Concurrency Gap

**Severity:** MEDIUM  
**Status:** OPEN  
**Depends On:** ADR-R12-01

**Problem**

The design intent and comments describe bounded parallel command execution, but the current loop awaits each command serially.

**Evidence**

- `hm-agent.json` exposes `MaxConcurrentCommands`
- `src/HomeManagement.Agent/Communication/AgentCommandExecutionService.cs` now enforces the configured bounded concurrency limit
- `tests/HomeManagement.Agent.Tests/Communication/AgentCommandExecutionServiceTests.cs` verifies that execution is capped at the configured maximum while still draining queued work

**Implementation Goals**

- implement actual bounded concurrency or simplify the design and config to explicit single-threaded execution
- validate command ordering, cancellation, and resource limits under load

**Closure Criteria**

- implementation matches documented concurrency model
- test coverage exists for multiple simultaneous commands
- config surface matches actual runtime behavior

**Completion Notes**

- agent command execution has been extracted into a bounded execution service
- the configured maximum is clamped to at least one concurrent command to avoid invalid semaphore construction
- focused unit tests cover the concurrency cap directly

### REV12-06 — Development Defaults Leaking Into Primary Deployment Artifacts

**Severity:** MEDIUM  
**Status:** RESOLVED  
**Depends On:** ADR-R12-04

**Problem**

Primary deployment artifacts still embed development-oriented defaults or contradictory ingress behavior.

**Evidence**

- `deploy/kubernetes/secrets.yaml` remains reference-only and no longer defines the supported production path
- `deploy/helm/homemanagement/templates/ingress.yaml` uses explicit SSL redirect configuration aligned to chart values
- `deploy/helm/homemanagement/templates/secrets.yaml` now requires production secret values at render time
- `.github/workflows/ci.yml` and `.github/workflows/release.yml` now explicitly validate both render success with overrides and render failure without required values

**Implementation Goals**

- clearly separate dev-only defaults from supported production deployment assets
- make TLS behavior consistent with ingress configuration
- make secret placeholders fail-fast in CI or chart validation

**Closure Criteria**

- supported deployment path contains no ambiguous dev/prod defaults
- ingress and TLS behavior are internally consistent
- placeholder secrets are rejected before deployment

**Completion Notes**

- `helm template` now fails immediately if `database.connectionString`, `auth.jwtSigningKey`, or `agentGateway.apiKey` are omitted
- CI and release workflows validate both the failing and successful render paths
- the supported deployment boundary remains Helm-only, with raw manifests preserved as reference artifacts

---

## 5. Review Log

| Date | Source | Outcome |
|---|---|---|
| 2026-03-21 | Independent senior review | 6 findings opened, 4 architecture decisions required |

---

## 6. Usage Guidance

When a remediation item is addressed:

1. Update the finding status in this document.
2. Record the deciding architecture choice in the matching ADR row.
3. Update the affected architecture docs.
4. Add or update tests that prove the issue is closed.
5. Add a new revision entry to `14-ARCHITECTURE-VALIDATION.md` summarizing what changed.