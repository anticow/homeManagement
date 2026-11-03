# Contributing to HomeManagement

Thank you for your interest in contributing! This guide covers everything you need to get started.

## Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| .NET SDK | 8.0+ | [Download](https://dot.net/download) |
| Docker | 24+ | Required for integration tests (TestContainers) |
| Git | 2.30+ | |
| SQL Server | 2022 (via Docker) | Only for integration tests |

## Quick Start

```bash
# Clone the repository
git clone https://github.com/<owner>/homeManagement.git
cd homeManagement

# Restore & build
dotnet build HomeManagement.sln

# Run unit tests (no Docker required)
dotnet test HomeManagement.sln --filter "FullyQualifiedName!~Integration"

# Run all tests including integration (requires Docker)
dotnet test HomeManagement.sln
```

### Focused CI regression checks

For the agent process cancellation and broker automation host lifecycle tests, run:

```bash
dotnet test tests/HomeManagement.Agent.Tests -c Release --filter "FullyQualifiedName~AgentProcessRunnerTests|FullyQualifiedName~ManagedServicesTests"
dotnet test tests/HomeManagement.AgentGateway.Host.Tests -c Release --filter "FullyQualifiedName~AgentApiKeyValidatorTests"
dotnet test tests/HomeManagement.Web.Tests -c Release --filter "FullyQualifiedName~AutomationPlanEndpointsIntegrationTests"
```

These tests do not require Docker or live services. Run the process tests on Windows and
Linux, and the automation endpoint tests on Linux as well as Windows before declaring
a cross-platform CI failure resolved. The cancellation test allows up to 60 seconds
for shell startup on busy runners, then independently requires cancellation and child
exit within five seconds each. Test hosts must use host-scoped environment configuration
and idempotent teardown rather than changing the process environment or stopping a host twice.
The automation endpoint fixture replaces gateway interfaces and removes only the
agent auto-registration observer, so no external gateway capability probe can stop its host.
Transport observer tests separately cover capability connection failures and
enrollment-mode rejection. Production still fails closed on unverifiable authority;
these endpoint tests must not change the host's background-service exception policy.

## Project Structure

The solution contains **35 projects** (19 source + 16 test):

### Source Projects (`src/`)

| Project | Description |
|---------|-------------|
| `HomeManagement.Abstractions` | Shared interfaces, models, and contracts |
| `HomeManagement.Core` | Cross-cutting: retry policies, validation, encryption |
| `HomeManagement.Data` | EF Core DbContext, repositories, migrations |
| `HomeManagement.Data.SqlServer` | SQL Server provider and configuration |
| `HomeManagement.Transport` | SSH/WinRM/gRPC remote execution providers |
| `HomeManagement.Agent` | On-machine agent (gRPC service, commands) |
| `HomeManagement.Inventory` | Machine inventory and metadata management |
| `HomeManagement.Patching` | OS patch detection, application, and history |
| `HomeManagement.Services` | OS service control (systemd / Windows Services) |
| `HomeManagement.Vault` | Encrypted credential storage with pinned memory |
| `HomeManagement.Auditing` | Immutable audit log with chain hashing |
| `HomeManagement.Orchestration` | Job scheduling, idempotency, retry |
| `HomeManagement.Auth` | JWT authentication and RBAC authorization |
| `HomeManagement.Gui` | Avalonia desktop UI (MVVM, reactive) |
| `HomeManagement.Broker.Host` | Central API service (ASP.NET Core) |
| `HomeManagement.Auth.Host` | Authentication service |
| `HomeManagement.AgentGateway.Host` | gRPC gateway for agent communication |
| `HomeManagement.Gateway` | YARP reverse-proxy / API gateway |
| `HomeManagement.Web` | Blazor Server dashboard |

### Test Projects (`tests/`)

Each source project with business logic has a corresponding test project. Integration tests use TestContainers for SQL Server.

## Coding Conventions

- **Target framework:** .NET 8 / C# 12
- **Warnings as errors:** `TreatWarningsAsErrors=true` — the build must have zero warnings.
- **Central package management:** All NuGet versions are declared in `Directory.Packages.props` at the solution root. Do not add `Version` attributes in individual `.csproj` files.
- **Code analysis:** `EnforceCodeStyleInBuild=true` with `AnalysisLevel=latest-all`.
- **Nullable reference types:** Enabled globally.
- **File-scoped namespaces:** Preferred throughout.
- **Records / sealed classes:** Use records for DTOs and value objects; seal classes that aren't designed for inheritance.
- **Internal by default:** Service implementations are `internal`. Tests access them via `InternalsVisibleTo`.

## Architecture

The codebase follows a modular architecture with clear layering:

```
Abstractions ← Core ← Domain Modules ← Platform Services ← GUI
                         ↑
                    Data / Transport
```

**Key design decisions:**
- Domain modules depend only on Abstractions and Core — never on each other.
- Platform services (Broker, Auth, AgentGateway, Gateway, Web) compose domain modules via DI.
- The Transport layer provides SSH/WinRM/gRPC execution behind `IRemoteExecutor`.
- The Vault uses `GCHandle.Pinned` and `IDisposable` to zero sensitive memory.
- The Audit subsystem uses chain hashing for tamper detection.

See the `docs/architecture/` directory for 16 detailed architecture documents.

## Making Changes

1. **Create a branch** from `main` with a descriptive name (e.g., `feature/add-reboot-scheduling`).
2. **Write tests first** (or alongside) — aim for ≥ 80% coverage on new code.
3. **Build with zero warnings** — `dotnet build` must succeed cleanly.
4. **Run the full test suite** — all unit tests must pass.
5. **Keep commits focused** — one logical change per commit.

## Pull Request Process

1. Ensure the CI pipeline passes (build + unit tests).
2. Integration tests run in CI via Docker; they may be skipped locally if Docker is unavailable.
3. Update documentation in `docs/architecture/` if your change affects the system design.
4. PRs require at least one approving review.

## Running Services Locally

Each platform service has `appsettings.json` and `appsettings.Development.json`:

```bash
# Start the broker API
dotnet run --project src/HomeManagement.Broker.Host

# Start the auth service
dotnet run --project src/HomeManagement.Auth.Host

# Start the gateway (YARP proxy on port 8080)
dotnet run --project src/HomeManagement.Gateway

# Start the Blazor dashboard
dotnet run --project src/HomeManagement.Web
```

Default ports in Development:
- Broker API: `https://localhost:8082`
- Auth Service: `https://localhost:8083`
- Agent Gateway (gRPC): `http://localhost:9444`
- Gateway (YARP): `https://localhost:8080`
- Web Dashboard: `https://localhost:8443`

## Kubernetes Deployment

A Helm chart is provided in `deploy/helm/homemanagement/`:

```bash
helm install homemanagement deploy/helm/homemanagement \
  --set global.sqlServer.connectionString="Server=..." \
  --set global.auth.jwtSigningKey="<your-key>"
```

## Reporting Issues

- Use GitHub Issues with a clear title and reproduction steps.
- Include relevant logs (Serilog structured output in Development mode).
- Tag issues with appropriate labels (`bug`, `enhancement`, `documentation`).
