# 18 — Gap Analysis & Tracking

> **Status: CLOSED** — All gaps resolved as of 2026-04-20. Retained for historical reference.
>
> **Created**: 2026-03-22  
> **Last Updated**: 2026-04-20  
> **Purpose**: Track gaps between architecture design (Docs 01–17) and current implementation/deployment state, with actionable remediation items.

## Status Legend

| Status | Meaning |
|---|---|
| `OPEN` | Identified, not yet started |
| `IN-PROGRESS` | Actively being worked |
| `BLOCKED` | Requires external dependency or decision |
| `RESOLVED` | Fix verified and merged |

---

## P0 — Blocking End-to-End Dev Testing

### GAP-01: Docker Compose Missing Seq Service

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P0 |
| **Area** | Local Dev Environment |
| **Files** | `deploy/docker/docker-compose.yaml` |
| **Refs** | Doc-04 (Technology Stack), Doc-15 (Platform Architecture) |

**Problem**: All five platform services configure `Serilog.Sinks.Seq` targeting `http://seq:5341`, but no `seq` service exists in docker-compose.yaml. Structured logging silently fails on `docker compose up`.

**Remediation**:
- [x] Add `datalust/seq:latest` service to docker-compose with `ACCEPT_EULA=Y`, ports `5380:80` (UI) and `5341:5341` (ingestion)
- [x] Add `depends_on: seq` to all five application services
- [x] Add `Seq__Url` env var to all application services

**Resolution (2026-03-23)**: Added Seq service with persistent volume. All services now depend on Seq and inject `Seq__Url=http://seq:5341`.

---

### GAP-02: Docker Compose Lacks Health Checks and Startup Ordering

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P0 |
| **Area** | Local Dev Environment |
| **Files** | `deploy/docker/docker-compose.yaml` |
| **Refs** | Doc-05 (Deployment & Scaling) |

**Problem**: Services use `depends_on: sqlserver` without health-check conditions. SQL Server takes several seconds to accept connections after container start. Broker and Auth will crash-loop attempting DB initialization before SQL Server is ready.

**Remediation**:
- [x] Add healthcheck to `sqlserver` service using `/opt/mssql-tools18/bin/sqlcmd -C -Q "SELECT 1"` with interval/retries
- [x] Change `depends_on` on broker, auth to `condition: service_healthy`
- [x] Add healthcheck stanzas to application services using their `/healthz` endpoints

**Resolution (2026-03-23)**: SQL Server has `sqlcmd` health check (30s start_period, 10 retries). Broker/Auth depend on `sqlserver: service_healthy`. Web/Gateway depend on `broker: service_healthy` + `auth: service_healthy`. All app services have `/healthz` curl checks.

---

### GAP-03: Gateway YARP Destination Key Mismatch in Docker Compose

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P0 |
| **Area** | Local Dev Environment |
| **Files** | `deploy/docker/docker-compose.yaml`, `src/HomeManagement.Gateway/appsettings.json` |
| **Refs** | Doc-15 (Platform Architecture) |

**Problem**: Gateway `appsettings.json` defines YARP destinations with key `primary`. The Helm chart also uses `primary`. However, docker-compose.yaml overrides with `broker-1` / `auth-1` as destination keys, which creates **new** destinations instead of overriding the existing ones.

```
# docker-compose (WRONG key)
ReverseProxy__Clusters__broker__Destinations__broker-1__Address
# appsettings.json + Helm (CORRECT key)
ReverseProxy__Clusters__broker__Destinations__primary__Address
```

**Remediation**:
- [x] Change docker-compose env vars to use `__primary__` as the destination key to match appsettings and Helm

**Resolution (2026-03-23)**: Updated env vars to `ReverseProxy__Clusters__broker__Destinations__primary__Address` and `ReverseProxy__Clusters__auth__Destinations__primary__Address`.

---

### GAP-04: No Docker Compose Run Automation or Documentation

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P0 |
| **Area** | Local Dev Environment, Developer Experience |
| **Files** | `deploy/docker/`, `start.ps1` |
| **Refs** | Doc-05 (Deployment & Scaling), Doc-16 (CI/CD) |

**Problem**: `start.ps1` launches the desktop GUI + Agent (local mode). There is no script, README, or documented path for a developer to run the full platform stack (Web + Broker + Auth + Gateway + AgentGW + SQL Server) via docker-compose.

**Remediation**:
- [x] Create `start-platform.ps1` that runs `docker compose up --build`, waits for all health checks, and optionally opens the browser
- [ ] ~~Alternatively, add a `-Docker` switch to `start.ps1`~~ (separate script preferred)
- [x] Add `deploy/docker/README.md` documenting local platform setup

**Resolution (2026-03-23)**: Created `start-platform.ps1` (build → start → health wait → browser open, with `-Detach` and `-NoBrowser` switches). Created `deploy/docker/README.md` with service table, quick start, manual commands, startup order, and dev credentials.

---

### GAP-05: EF Core Migration Timing in Docker Compose

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P0 |
| **Area** | Local Dev Environment |
| **Files** | `deploy/docker/docker-compose.yaml`, `src/HomeManagement.Core/ServiceRegistration.cs` |
| **Refs** | Doc-10 (Data Architecture) |

**Problem**: In production, Ansible runs `dotnet ef database update` before the services start. In docker-compose, the database is empty. Broker.Host calls `ServiceRegistration.InitializeDatabaseAsync` which runs `MigrateAsync()` — confirmed working. However, two bugs were found:
1. `AddHomeManagement()` unconditionally registered a SQLite `DbContext`, overriding the SQL Server registration from `AddHomeManagementSqlServer()`
2. `InitializeDatabaseAsync` executed SQLite-specific `PRAGMA journal_mode=WAL` regardless of provider

**Remediation**:
- [x] Verified `Broker.Host` calls `MigrateAsync()` on startup — migrations apply automatically
- [x] Fixed `AddHomeManagement()` to skip SQLite `DbContext` registration when a provider is already registered (e.g., SQL Server)
- [x] Fixed `InitializeDatabaseAsync()` to only run `PRAGMA journal_mode=WAL` when the provider `IsSqlite()`
- [x] Full solution build + all tests pass

**Resolution (2026-03-23)**: `ServiceRegistration.cs` now checks `services.Any(s => s.ServiceType == typeof(DbContextOptions<HomeManagementDbContext>))` before registering SQLite, and uses `db.Database.IsSqlite()` guard for the PRAGMA. Platform hosts call `AddHomeManagementSqlServer()` first → `AddHomeManagement()` skips SQLite → SQL Server migrations run correctly.

---

## P1 — Required Before Production E2E

### GAP-06: Seq Not Deployed in Kubernetes

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P1 |
| **Area** | K8s Deployment, Observability |
| **Files** | `deploy/helm/homemanagement/values.yaml`, Ansible `bootstrap-platform-prereqs.yml` |
| **Refs** | Doc-04, Doc-15, Doc-16 |

**Problem**: All Helm deployment templates inject `Seq__Url` from `{{ .Values.seq.url }}` (default: `http://seq:5341`). There is no Seq Deployment, Service, or Helm subchart in the platform chart. Ansible does not deploy Seq. All structured logging is silently lost in K8s after deploy.

**Remediation**:
- [ ] ~~Option A: Add Seq as a subchart dependency in `Chart.yaml`~~
- [ ] ~~Option B: Add a Seq deployment template to the existing chart~~
- [x] Option C: Add a `seq` Ansible role to `bootstrap-platform-prereqs.yml`
- [x] Validate `seq.seq.svc.cluster.local:5341` resolves in-cluster after deployment

**Resolution (2026-03-23)**: Created Ansible role `roles/seq/` that deploys Seq via the official `datalust/seq` Helm chart with persistent storage, optional ingress. Added `seq` to `bootstrap-platform-prereqs.yml` role list. Updated Helm `values.yaml` and `homemanagement` role default to use FQDN `http://seq.seq.svc.cluster.local:5341`. Syntax check passed.

---

### GAP-07: SQL Server Not Managed via IaC

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P1 |
| **Area** | K8s Deployment, Infrastructure |
| **Files** | Ansible `deploy-homemanagement.yml`, Helm `values.yaml` |
| **Refs** | Doc-10, Doc-15 |

**Problem**: SQL Server is referenced as an external dependency. Helm chart requires `database.connectionString` (no default). Ansible `homemanagement_sql` role runs EF migrations against the connection string from vault, but does not provision the SQL Server instance itself. New environment setup requires manual SQL Server provisioning.

**Remediation**:
- [ ] ~~Option A: Add a SQL Server StatefulSet to the Helm chart~~
- [ ] ~~Option B: Create an Ansible role `mssql_server`~~
- [x] Option C: Document SQL Server as an external prerequisite with connection string format and minimum version requirements
- [x] Existing `migrations list --connection` task already validates SQL connectivity before applying

**Resolution (2026-03-23)**: Documented SQL Server as an external prerequisite in `ansible/README.md` (External prerequisites table). Added documentation comments to `homemanagement_sql/defaults/main.yml` (minimum version, connection string format). The role's existing `migrations list` task provides connectivity validation — no additional task needed.

---

### GAP-08: Agent mTLS Certificate Lifecycle Not Automated

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P1 |
| **Area** | Agent Deployment, Security |
| **Files** | `certs/`, `hm-agent.json`, `scripts/New-AgentCert.ps1` |
| **Refs** | Doc-09 (Security Architecture), Doc-11 (Agent Architecture) |

**Problem**: Dev certs (`agent.pfx`, `ca.crt`, `server.pfx`) are static files in `certs/`. No generation script, rotation mechanism, or automation for provisioning new agent certificates from a private CA. Helm `certificate.yaml` handles ingress TLS via cert-manager but **not** agent mTLS.

**Remediation**:
- [x] Create `scripts/New-AgentCert.ps1` that generates a CA (if not exists) and issues agent + server certs
- [x] Document the cert structure (CA → server cert for AgentGW, CA → agent cert per agent)
- [ ] Add cert provisioning to the Agent deployment workflow (Ansible task or manual procedure) — *deferred to operational phase*
- [ ] Define rotation cadence and add cert expiry monitoring — *deferred to operational phase*

**Resolution (2026-03-23)**: Created `scripts/New-AgentCert.ps1` with: private CA generation (RSA-4096, 10yr), server cert with SAN (AgentGW), agent cert with client auth extension. Supports `-AgentId`, `-ServerSAN`, `-CertValidityDays`, `-NoClobber`. Outputs `certs/{ca.crt, ca.key, server.pfx, agent.pfx}`. Certificate structure documented in script output.

---

### GAP-09: Ansible Inventory Environments Are Identical

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P1 |
| **Area** | Ansible, Environment Management |
| **Files** | `inventories/cowgo/`, `inventories/dev/` |
| **Refs** | Doc-05 (Deployment & Scaling) |

**Problem**: `cowgo` and `local` inventories have identical `hosts` files and `group_vars`. `Use-Inventory.ps1` copies a named inventory to `local/`, but this provides no environment differentiation. There is no dev/staging vs. production separation.

**Remediation**:
- [x] Created `inventories/dev/` with: single localhost node, no workers, reduced resource allocations, non-secret dev credentials, `NodePort` ingress, no TLS issuer
- [x] `dev` inventory uses `ansible_connection=local` for local testing
- [x] `cowgo` remains the production/staging environment
- [x] Updated `README.md` with environment table and activation instructions

**Resolution (2026-03-23)**: Created `inventories/dev/` (hosts, group_vars/all/{common,homemanagement,platform_prereqs}.yml, k3s_control_plane.yml). Dev environment uses localhost, NodePort, no cert-manager issuer, docker-compose-matching dev credentials. Updated Ansible README with available environments table. Inventory graph validated via Docker.

---

## P2 — Important for Operational Readiness

### GAP-10: Release Pipeline Image Publishing Unverified

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P2 |
| **Area** | CI/CD |
| **Files** | `.github/workflows/release.yml` |
| **Refs** | Doc-16 (CI/CD Architecture) |

**Problem**: Helm chart and Ansible reference `ghcr.io/anticow/hm-broker:latest` (and similar). The release workflow exists but it's unverified whether it actually builds and pushes Docker images to GHCR. If images don't exist, Helm deploy fails with `ImagePullBackOff`.

**Remediation**:
- [x] Verify `release.yml` contains Docker build + push steps for all 6 images (broker, auth, web, gateway, agent-gw, agent)
- [ ] Verify GHCR credentials are configured in GitHub repo secrets — *requires runtime verification*
- [ ] Tag and push a test release to confirm images appear in `ghcr.io/anticow/` — *requires runtime verification*
- [x] Confirm image names in release workflow match Helm `values.yaml` and Ansible `homemanagement` role defaults

**Resolution (2026-03-23)**: Verified `release.yml` builds all 6 Docker images via matrix (hm-agent, hm-broker, hm-auth, hm-web, hm-gateway, hm-agent-gw), pushes to `ghcr.io/${{ github.repository_owner }}/hm-*` with version + latest tags. Image names match Helm values.yaml `image.repository` fields. GHCR credentials use `secrets.GITHUB_TOKEN` (automatic). Runtime verification deferred to first tag push.

---

### GAP-11: No Platform Integration Test Path

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P2 |
| **Area** | Testing |
| **Files** | `tests/HomeManagement.Platform.Tests/`, `.github/workflows/ci.yml` |
| **Refs** | Doc-04, Doc-14 (Architecture Validation) |

**Problem**: `HomeManagement.Integration.Tests` exists but there is no documented or automated way to run integration tests against a running docker-compose stack. Current tests use Testcontainers for unit-style DB integration, not full platform E2E (Web → Gateway → Broker → Auth → DB).

**Remediation**:
- [x] Add E2E smoke tests that exercise the full request chain (login → API call → verify response)
- [x] Wire platform smoke tests into CI as a separate job (after unit tests + docker builds pass)

**Resolution (2026-03-23)**: Created `tests/HomeManagement.Platform.Tests/` with `PlatformSmokeTests.cs` — 14 xUnit tests tagged `[Trait("Category", "Platform")]` covering: health checks (5 services), readiness (Broker/Auth), auth flow (invalid login → 400, admin login → token), gateway routing (auth route, broker 401), full authenticated chain (login → JWT → list machines), Web HTML response, Prometheus `/metrics` endpoints. Added `platform-smoke-tests` CI job in `ci.yml` that: builds docker-compose, starts stack, waits for health, runs tests, collects logs on failure, tears down.

---

### GAP-12: Prometheus / Monitoring Stack Not Deployed

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P2 |
| **Area** | Observability, K8s Deployment |
| **Files** | `deploy/helm/homemanagement/values.yaml`, Ansible `roles/monitoring/` |
| **Refs** | Doc-04, Doc-15 |

**Problem**: All services expose `/metrics` via OpenTelemetry Prometheus exporter. Helm values include `metrics.serviceMonitor.enabled: false`. No Prometheus, Grafana, or monitoring stack is deployed by Ansible or Helm.

**Remediation**:
- [x] Add `kube-prometheus-stack` to `bootstrap-platform-prereqs.yml` (Ansible)
- [x] Create ServiceMonitor for HomeManagement services in the monitoring role
- [ ] Add custom Grafana dashboards for service health, request rates, error rates — *deferred to operational phase*

**Resolution (2026-03-23)**: Created Ansible role `roles/monitoring/` that deploys `kube-prometheus-stack` via Helm (Prometheus with persistent storage, Grafana with optional ingress, Alertmanager disabled by default). Role creates a `ServiceMonitor` CR matching `app.kubernetes.io/part-of: homemanagement` labels to scrape `/metrics` from all services. Added `monitoring` to `bootstrap-platform-prereqs.yml`. Helm chart's `metrics.serviceMonitor.enabled` can be set to `true` once CRDs are installed. Custom dashboards deferred.

---

### GAP-13: ArgoCD Root-App External Dependency Unverifiable

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P2 |
| **Area** | GitOps, Deployment |
| **Files** | Ansible `roles/argocd/tasks/main.yml`, `deploy/helm/homemanagement/argocd-application.reference.yaml` |
| **Refs** | Doc-16 (CI/CD Architecture) |

**Problem**: ArgoCD role deploys a root Application pointing to `git@github.com:anticow/argocd.git` at path `charts/root-app/`. This private repo is not in the workspace and cannot be verified. It's unknown whether HomeManagement is defined as an ArgoCD Application in that repo.

**Remediation**:
- [ ] Verify the `argocd.git` repo contains an ArgoCD Application CR for HomeManagement — *requires access to external repo*
- [x] Document the ArgoCD app-of-apps structure in this workspace via reference manifest
- [x] Created a co-located ArgoCD Application reference manifest

**Resolution (2026-03-23)**: Created `deploy/helm/homemanagement/argocd-application.reference.yaml` — a documented reference Application CR showing the expected ArgoCD config (source: `deploy/helm/homemanagement`, destination: `homemanagement` namespace, automated sync with prune + selfHeal). File is NOT applied directly — serves as documentation and template for the external ArgoCD repo. The external repo verification is deferred to runtime access.

---

### GAP-14: Docker Compose / Helm Auth Configuration Inconsistency

| Field | Value |
|---|---|
| **Status** | `RESOLVED` |
| **Severity** | P2 |
| **Area** | Local Dev Environment, K8s Deployment |
| **Files** | `deploy/docker/docker-compose.yaml`, `deploy/helm/homemanagement/values.yaml` |
| **Refs** | Doc-15 |

**Problem**: In docker-compose, the `web` service sets `AuthApi__BaseUrl` and `BrokerApi__BaseUrl` which match `Web/Program.cs` Refit configuration bindings (confirmed correct). However, Helm `values.yaml` had `auth.audience: "homemanagement"` while the code default in `AuthOptions.cs` is `"homemanagement-api"`. This mismatch would cause JWT validation failures in K8s.

**Remediation**:
- [x] Verified `Web/Program.cs` configuration bindings match docker-compose env vars (`BrokerApi__BaseUrl`, `AuthApi__BaseUrl`, `Seq__Url`)
- [x] Fixed Helm `values.yaml` `auth.audience` from `"homemanagement"` to `"homemanagement-api"` to match `AuthOptions.cs` default
- [x] Docker-compose already had correct `Auth__Audience: "homemanagement-api"` on Broker, Auth, and Gateway

**Resolution (2026-03-23)**: Web config bindings verified correct. Fixed Helm audience mismatch. All three deployment surfaces (docker-compose, Helm, code defaults) now consistently use `issuer: "homemanagement"` and `audience: "homemanagement-api"`.

---

## Summary

| Priority | Open | In-Progress | Resolved | Total |
|---|---|---|---|---|
| **P0** | 0 | 0 | 5 | 5 |
| **P1** | 0 | 0 | 4 | 4 |
| **P2** | 0 | 0 | 5 | 5 |
| **Total** | **0** | **0** | **14** | **14** |

### Deferred Items (Require Runtime Verification)

| Gap | Item | Reason |
|---|---|---|
| GAP-08 | Cert provisioning in Agent deployment workflow | Operational phase |
| GAP-08 | Cert rotation cadence + expiry monitoring | Operational phase |
| GAP-10 | Verify GHCR push via tag + release | Requires first production tag |
| GAP-12 | Custom Grafana dashboards | Operational phase |
| GAP-13 | Verify external ArgoCD repo Application CR | Requires access to `anticow/argocd` repo |
