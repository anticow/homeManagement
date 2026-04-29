# GitHub Copilot Instructions — HomeManagement

> This repo (`homeManagement`) and its companion infrastructure repo (`ansible`) are deployed together.
> The app is packaged as Docker images, published to `ghcr.io/anticow`, and deployed onto a K3s cluster
> via the Helm chart in `deploy/helm/homemanagement/`. The `ansible` repo drives all infrastructure
> provisioning and application deployment.

## Build, Test & Lint

```bash
# Restore & build (must have zero warnings — TreatWarningsAsErrors=true)
dotnet build HomeManagement.sln

# Unit tests only (no Docker required)
dotnet test HomeManagement.sln --filter "FullyQualifiedName!~Integration"

# All tests including integration (requires Docker for TestContainers/SQL Server)
dotnet test HomeManagement.sln

# Run a single test class or method
dotnet test HomeManagement.sln --filter "FullyQualifiedName~MyClassName.MyMethodName"

# Check formatting (CI gate)
dotnet format --verify-no-changes --verbosity diagnostic

# Apply formatting fixes
dotnet format
```

Run the full local stack (SQL Server + Seq + all services):
```bash
# Requires HM_SQL_SA_PASSWORD, HM_JWT_SIGNING_KEY, HM_AGENT_GATEWAY_API_KEY env vars
docker compose -f deploy/docker/docker-compose.yaml up
```

Run individual services locally (Development profile, SQLite):
```bash
dotnet run --project src/HomeManagement.Broker.Host   # https://localhost:8082
dotnet run --project src/HomeManagement.Auth.Host     # https://localhost:8083
dotnet run --project src/HomeManagement.Gateway       # https://localhost:8080  (YARP)
dotnet run --project src/HomeManagement.Web           # https://localhost:8443  (Blazor)
dotnet run --project src/HomeManagement.Gui           # Avalonia desktop app
```

## Application Architecture

```
Abstractions ← Core ← Domain Modules ← Platform Services ← GUI/Web
                           ↑
                      Data / Transport
```

**Layer rules:**
- `HomeManagement.Abstractions` — interfaces, DTOs, enums, repository contracts. No implementation logic.
- `HomeManagement.Core` — DI composition root (`ServiceRegistration`), cross-cutting infrastructure (logging, correlation, health).
- **Domain modules** (`Inventory`, `Patching`, `Services`, `Auditing`, `Vault`, `Orchestration`, `Transport`, `Agent`, `Automation`) — depend only on `Abstractions` and `Core`, never on each other.
- **Platform services** (`Broker.Host`, `Auth.Host`, `AgentGateway.Host`, `Gateway`, `Web`) — compose domain modules through DI. They may also override the database provider (e.g., SQL Server instead of SQLite).
- `HomeManagement.Gui` — Avalonia MVVM/ReactiveUI desktop app, depends on domain modules.

**Module registration pattern:** Each domain assembly contains an `IModuleRegistration` implementation. `ServiceRegistration.AddHomeManagement()` in `Core` auto-discovers and invokes all of them by scanning `HomeManagement.*.dll` files — no manual wiring in host projects needed. Add a new module by implementing `IModuleRegistration` in the module assembly.

**Database:** SQLite by default (path passed to `AddHomeManagement(dataDirectory)`). Platform hosts register SQL Server via `HomeManagement.Data.SqlServer` before calling `AddHomeManagement()`; `ServiceRegistration` detects the existing `DbContextOptions<HomeManagementDbContext>` and skips its SQLite fallback. Integration tests spin up SQL Server via `Testcontainers.MsSql`. EF Core migrations are in `HomeManagement.Data` and applied by Ansible (`homemanagement_sql` role) before each Helm deploy.

**Transport abstraction:** All remote execution flows through `IRemoteExecutor.ExecuteAsync / TransferFileAsync / TestConnectionAsync`. Implementations: SSH (`SSH.NET`), WinRM/PS Remoting, and gRPC agent. Callers are transport-agnostic.

**Audit subsystem:** `AuditLoggerService` links every event to the previous via HMAC-SHA256 (`previousHash|eventId|timestamp|action|actor|outcome`), enabling tamper detection. Sensitive fields are auto-redacted by `ISensitiveDataFilter` before any record is persisted or logged.

**Vault / security:** `VaultCrypto` uses Argon2id (64 MiB, 3 iterations, 4 threads) + AES-256-GCM. Key material is zeroed with `CryptographicOperations.ZeroMemory` in `finally` blocks. `CredentialVaultService` pins byte arrays in memory with `GCHandle.Pinned` so the GC cannot scatter secrets.

**Automation module:** `HomeManagement.Automation` provides `IAutomationEngine` with named workflows (`fleet.health_report`, `fleet.patch_all`, `service.ensure_running`, `haos.health_status`, `haos.entity_snapshot`, `ansible.handoff`). Workflows run fire-and-forget via `Task.Run` with their own DI scopes; results are persisted through `IAutomationRunRepository`. The module has a `DisabledAutomationEngine` null implementation registered when `AutomationOptions.Enabled = false`. `IHaosAdapter` (Home Assistant OS) and `AnsibleHandoffService` are collaborators — both have null implementations for environments without those integrations.

**AI / LLM integration:** `HomeManagement.AI.Abstractions` defines `ILLMClient`, `IPlanner`, and `ISummarizer`. `HomeManagement.AI.Ollama` provides the Ollama implementation (`OllamaLlmClient`). `IWorkflowPlanner` in the Automation module translates natural-language objectives into `WorkflowPlan` structs via the configured `ILLMClient`. Each AI assembly self-registers through `IModuleRegistration` — no host wiring required.

## Custom Copilot Agent

`.github/agents/platform-drift-sentinel.agent.md` — invoke when you need to detect configuration or deployment drift between the repository and the running platform. It uses read-only `kubectl`, `helm`, and `docker compose` commands and produces a structured drift report. **Never run mutating commands** (`kubectl apply`, `helm upgrade`, `ansible-playbook`) inside that agent.

## Deployment Architecture

```
[CI: build + push images → ghcr.io/anticow/hm-*]
         ↓
[Ansible: deploy-homemanagement.yml on K3s control plane]
    ├── homemanagement_sql role  → dotnet ef database update → SQL Server at sql.cowgomu.net
    └── homemanagement role      → helm upgrade/install using chart from this repo
```

**Production cluster (cowgo):** K3s on Proxmox VMs — 1 control plane + 3 workers at `cowgomu.net`. Managed by the `ansible` repo.

**Public endpoints (prod):**
| Service | Host |
|---|---|
| Blazor Web | `homemanagement.cowgomu.net` |
| Broker API | `broker.cowgomu.net` |
| Auth API | `authapi.cowgomu.net` |
| Agent Gateway (gRPC) | `agentgw.cowgomu.net` |
| Agent Gateway API | `agentgw-api.cowgomu.net` |

**Helm chart** (`deploy/helm/homemanagement/`): The chart requires three values that have no defaults — `database.connectionString`, `auth.jwtSigningKey`, and `agentGateway.apiKey`. The CI Helm lint job supplies dummy values; real values come from the Ansible vault. The chart renders a cert-manager `Certificate` resource for all five hostnames; `ingress.clusterIssuer` must be set to a valid ClusterIssuer name (in production this is the Azure DNS-backed `letsencrypt-dns-sp-v2`).

**Docker images:** Six images built from Dockerfiles in `docker/`: `broker.Dockerfile`, `auth.Dockerfile`, `web.Dockerfile`, `gateway.Dockerfile`, `agent-gw.Dockerfile`, `agent.Dockerfile`. All are built in CI (`docker-build-validate` job) and pushed to `ghcr.io/anticow` in the release workflow.

**Local stack** (`deploy/docker/docker-compose.yaml`): Brings up SQL Server 2022 + Seq + all platform services. Used by the `platform-smoke-tests` CI job and for local integration testing. Requires env vars `HM_SQL_SA_PASSWORD`, `HM_JWT_SIGNING_KEY`, `HM_AGENT_GATEWAY_API_KEY`.

## C# Conventions

- **Target framework:** .NET 8 / C# 12
- **Zero warnings:** `TreatWarningsAsErrors=true` — the build must be clean
- **Nullable reference types:** Enabled globally; never suppress without an explanatory comment
- **File-scoped namespaces:** `namespace Foo.Bar;` throughout — not block-scoped
- **Records / sealed:** Records for DTOs and value objects; seal classes not designed for inheritance
- **Visibility:** Service implementations are `internal sealed`; test projects access them via `InternalsVisibleTo`
- **`CA1707` suppression:** Only in test projects — xUnit method naming uses underscores (`Method_Scenario_Expected`)
- **NuGet versions:** Declared exclusively in `Directory.Packages.props`. Never add `Version=` to individual `.csproj` files.
- **Logging:** Serilog structured properties only — no string interpolation in log calls. `LoggingBootstrap.AddHomeManagementLogging()` must be called before the DI container is built.
- **Testing stack:** xUnit + NSubstitute + FluentAssertions. Integration tests tagged `Category=Integration`; platform tests tagged `Category=Platform`.

## DRY Principles

- **One interface, one place** — all contracts live in `HomeManagement.Abstractions`. Never redefine a DTO or interface in a domain module when an existing one in Abstractions fits.
- **Module registration is self-contained** — add a new module by implementing `IModuleRegistration` in its assembly. Do not add wiring in `ServiceRegistration` or host `Program.cs` files; auto-discovery handles it.
- **Shared cross-cutting behavior belongs in Core** — retry policies, validation helpers, logging bootstrap, and correlation middleware live in `HomeManagement.Core`. Don't copy these patterns into individual modules.
- **Options pattern for config** — all configuration values are bound via `IOptions<T>` to a typed options class. Never access `IConfiguration` directly in a service; never hardcode environment-specific values.
- **Extension methods over repeated setup code** — if the same DI registration or middleware setup appears in more than one host project, extract it to an extension method in the appropriate library project.
- **No duplicated EF mappings** — entity configurations use `IEntityTypeConfiguration<T>` classes, one per entity, in `HomeManagement.Data`. Don't re-map the same entity in a derived context.
- **Repository pattern is the DB boundary** — domain modules call repository interfaces from Abstractions; they never reference `HomeManagementDbContext` directly. Keep query logic in the repository implementation, not scattered across services.
- **Test helpers are shared** — common test fixtures, builder helpers, and fake implementations live in a shared test support project. Don't duplicate fake/stub implementations across test projects.

## CI Gates (all must pass)

1. `dotnet build` — zero warnings
2. `dotnet format --verify-no-changes`
3. Unit + integration tests (TestContainers)
4. Helm lint (`deploy/helm/homemanagement/`)
5. Docker image builds for all six services
6. Platform smoke tests (full Docker Compose stack, `Category=Platform`)
