# 16 — CI/CD Architecture: GitHub & GitHub Actions

> Continuous integration and delivery pipeline using GitHub as the repository host
> and GitHub Actions as the automation platform.
>
> **Revision 1 — 2026-03-18:** Initial CI/CD architecture and workflow implementation.

---

## 1. Overview

HomeManagement uses **GitHub** as its source repository and **GitHub Actions** for all
CI/CD automation. The pipeline enforces build quality, runs the full test suite, checks
code formatting, and produces release artifacts for both desktop binaries and container
images.

### Goals

| Goal | Implementation |
|---|---|
| **Every commit validated** | CI workflow runs on every push and PR to `main` / `develop` |
| **Zero-warning policy enforced** | `TreatWarningsAsErrors=true` in `Directory.Build.props` — build fails on any warning |
| **Test regression safety** | All 192+ tests execute; results published as check annotations |
| **Code coverage tracked** | Coverlet collects `XPlat Code Coverage`; reports uploaded as artifacts |
| **Format consistency** | `dotnet format --verify-no-changes` enforces `.editorconfig` rules |
| **Automated releases** | Tag-triggered workflow produces binaries and Docker images |
| **Dependency freshness** | Dependabot opens grouped PRs weekly for NuGet and Actions updates |

---

## 2. Branching Strategy

```
main          ← production-ready, protected
  └─ develop  ← integration branch, protected
       ├─ feature/*   ← new features
       ├─ bugfix/*    ← bug fixes
       └─ release/*   ← release stabilization
```

### Branch Protection Rules (configure in GitHub Settings)

| Rule | `main` | `develop` |
|---|---|---|
| Require PR before merge | Yes | Yes |
| Required status checks | `build-and-test`, `code-quality` | `build-and-test` |
| Require up-to-date branch | Yes | No |
| Require review approvals | 1+ | 1+ |
| Dismiss stale reviews | Yes | No |
| Restrict force push | Yes | Yes |
| Restrict deletions | Yes | Yes |

---

## 3. CI Workflow — `.github/workflows/ci.yml`

**Trigger:** Push to `main` or `develop`; pull requests targeting `main` or `develop`.

### Pipeline Stages

```
┌─────────────────────┐     ┌──────────────────────┐
│   build-and-test    │     │    code-quality       │
│                     │     │                       │
│  ┌───────────────┐  │     │  ┌────────────────┐   │
│  │   Checkout    │  │     │  │   Checkout     │   │
│  │   Setup .NET  │  │     │  │   Setup .NET   │   │
│  │   Restore     │  │     │  │   Restore      │   │
│  │   Build       │  │     │  │   Format Check │   │
│  │   Test        │  │     │  └────────────────┘   │
│  │   Upload TRX  │  │     │                       │
│  │   Upload Cov  │  │     │                       │
│  │   Report      │  │     │                       │
│  └───────────────┘  │     │                       │
└─────────────────────┘     └──────────────────────┘
        (parallel — no dependency between jobs)
```

### Key Features

| Feature | Detail |
|---|---|
| **Concurrency** | One run per ref; in-progress runs cancelled on new push |
| **Permissions** | Minimal: `contents: read`, `checks: write`, `pull-requests: write` |
| **Test reporter** | `dorny/test-reporter` parses TRX files → inline check annotations on PR |
| **Coverage** | Coverlet `XPlat Code Coverage` → Cobertura XML artifacts |
| **Timeout** | 15 min build-and-test, 10 min code-quality |
| **Runner** | `ubuntu-latest` (all projects are .NET 8 cross-platform) |

---

## 4. Release Workflow — `.github/workflows/release.yml`

**Trigger:** Push of a semver tag like `v1.0.0`, `v1.2.3-rc.1`.

### Pipeline Stages

```
                  ┌────────────┐
                  │  validate  │   Build + Test
                  └─────┬──────┘
                        │
              ┌─────────┴──────────┐
              │                    │
     ┌────────▼─────────┐  ┌──────▼──────────┐
     │ publish-binaries  │  │  docker-images  │
     │  (matrix: RID)    │  │  (matrix: svc)  │
     │  win-x64          │  │  agent          │
     │  linux-x64        │  │  broker         │
     └────────┬──────────┘  └──────┬──────────┘
              │                    │
              └─────────┬──────────┘
                        │
               ┌────────▼────────┐
               │ create-release  │
               │  GitHub Release │
               │  + artifacts    │
               └─────────────────┘
```

### Binary Publishing

| Output | RIDs | Options |
|---|---|---|
| **Agent** | `win-x64`, `linux-x64` | Self-contained, single-file, compressed |
| **GUI** | `win-x64`, `linux-x64` | Self-contained, single-file, compressed |

Artifacts are `.tar.gz` archives uploaded to the GitHub Release.

### Docker Images

| Image | Registry | Tags |
|---|---|---|
| `homemanagement-agent` | `ghcr.io` | `v{version}`, `latest` |
| `homemanagement-broker` | `ghcr.io` | `v{version}`, `latest` |

Docker builds are conditional — if the Dockerfile does not yet exist, the step is
skipped gracefully. This allows the workflow to exist before the Dockerfiles are
created during the platform migration (doc 15).

### Pre-release Detection

Tags containing a hyphen (e.g., `v1.0.0-rc.1`) are automatically marked as
pre-release on the GitHub Release.

---

## 5. Dependabot — `.github/dependabot.yml`

Automated dependency updates run weekly (Monday) for both NuGet packages and
GitHub Actions versions.

### Package Groups

| Group | Packages |
|---|---|
| `dotnet-extensions` | `Microsoft.Extensions.*` |
| `ef-core` | `Microsoft.EntityFrameworkCore*` |
| `grpc` | `Grpc.*`, `Google.Protobuf` |
| `avalonia` | `Avalonia*` |
| `serilog` | `Serilog*` |
| `quartz` | `Quartz*` |
| `testing` | `xunit*`, `Microsoft.NET.Test.Sdk`, `NSubstitute`, `FluentAssertions`, `coverlet.*` |

Grouped PRs reduce noise — a single PR updates all packages in a group together.
Maximum 10 NuGet PRs and 5 Actions PRs open at any time.

---

## 6. Repository Configuration

### Files Created

| File | Purpose |
|---|---|
| `.github/workflows/ci.yml` | CI pipeline — build, test, format check |
| `.github/workflows/release.yml` | Release pipeline — binaries, Docker, GitHub Release |
| `.github/dependabot.yml` | Automated dependency updates |
| `.github/pull_request_template.md` | Standard PR checklist |

### Pre-Existing Files

| File | Role in CI/CD |
|---|---|
| `Directory.Build.props` | `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild` — all enforced in CI |
| `Directory.Packages.props` | Central package management — Dependabot updates versions here |
| `.editorconfig` | Formatting rules — `dotnet format --verify-no-changes` enforces in CI |
| `.gitignore` | Excludes `bin/`, `obj/`, `certs/`, IDE files from commits |

---

## 7. Secrets & Environment Variables

### Required Secrets

| Secret | Used By | Description |
|---|---|---|
| `GITHUB_TOKEN` | release.yml | Auto-provided by GitHub; used for GHCR login and release creation |

No additional secrets are required for the current workflows. Future additions:

| Secret | When Needed | Purpose |
|---|---|---|
| `DOCKER_REGISTRY_*` | If using external registry | Push images to ACR/Docker Hub |
| `KUBE_CONFIG` | CD to Kubernetes | Deploy to cluster |
| `SQL_CONNECTION_STRING` | Integration tests | Test against real SQL Server |
| `CODECOV_TOKEN` | If adding Codecov | Upload coverage to Codecov.io |

---

## 8. Future Enhancements

### Phase A — Immediate (with platform work from doc 15)

| Enhancement | Trigger |
|---|---|
| Add Dockerfiles for Agent and Broker | When container images are ready |
| Add `web` image to Docker matrix | When Blazor web GUI project exists |
| Add `auth` image to Docker matrix | When Auth service project exists |
| Kubernetes deployment step | After Helm chart deployment credentials and environment approvals are available |

### Phase B — Maturity

| Enhancement | Description |
|---|---|
| **Code coverage gate** | Fail PR if coverage drops below threshold (e.g., 80%) |
| **SBOM generation** | `dotnet CycloneDX` for supply-chain security |
| **Container scanning** | Trivy or Grype scan on Docker images |
| **Staging environment** | Deploy to staging on merge to `develop` |
| **Production deployment** | Deploy to prod on merge to `main` (with approval gate) |
| **Performance tests** | Benchmark critical paths; fail on regression |
| **Matrix OS testing** | Run tests on `windows-latest` alongside `ubuntu-latest` |

### Phase C — Advanced

| Enhancement | Description |
|---|---|
| **GitHub Environments** | `staging` and `production` with required reviewers |
| **Deployment slots** | Blue-green or canary via Kubernetes |
| **Smoke tests** | Post-deploy health check hits `/healthz` endpoints |
| **Notification** | Slack/Teams webhook on release or failure |

---

## 9. Versioning Strategy

### Semantic Versioning

```
v{major}.{minor}.{patch}[-{prerelease}]

Examples:
  v1.0.0          ← stable release
  v1.1.0-rc.1     ← release candidate (marked pre-release on GitHub)
  v1.1.0          ← stable
  v2.0.0-alpha.1  ← breaking change preview
```

### Tagging Workflow

```bash
# Create a release
git tag v1.0.0
git push origin v1.0.0

# Create a pre-release
git tag v1.1.0-rc.1
git push origin v1.1.0-rc.1
```

The version number from the tag is extracted and injected into the published
binaries via `-p:Version=...` so assemblies and NuGet packages carry the
correct version metadata.

---

## 10. Local Development Parity

Developers can run the same checks locally before pushing:

```powershell
# Build (same as CI)
dotnet build --configuration Release

# Test (same as CI)
dotnet test --configuration Release

# Format check (same as CI code-quality job)
dotnet format --verify-no-changes

# Helm validation (same as CI/release deployment validation)
helm lint deploy/helm/homemanagement --set database.connectionString="Server=sql;Database=HomeManagement;User Id=sa;Password=ValidationPassword_123!;TrustServerCertificate=False;" --set auth.jwtSigningKey="validation-signing-key-that-is-long-enough-for-ci-checks-1234567890" --set agentGateway.apiKey="validation-agent-gateway-api-key"
helm template homemanagement deploy/helm/homemanagement --set database.connectionString="Server=sql;Database=HomeManagement;User Id=sa;Password=ValidationPassword_123!;TrustServerCertificate=False;" --set auth.jwtSigningKey="validation-signing-key-that-is-long-enough-for-ci-checks-1234567890" --set agentGateway.apiKey="validation-agent-gateway-api-key" > rendered-homemanagement.yaml

# Or use the project scripts
.\start.ps1                    # Build + test + launch
.\start.ps1 -SkipTests        # Build + launch (no tests)
```

---

## 11. Decision Log

| # | Decision | Rationale | Alternatives Considered |
|---|---|---|---|
| D1 | GitHub Actions over Azure DevOps | Repository is on GitHub; native integration with PRs, issues, GHCR | Azure DevOps Pipelines, Jenkins, GitLab CI |
| D2 | GHCR for container images | Free for public repos; `GITHUB_TOKEN` auth (no extra secrets) | Docker Hub, Azure Container Registry |
| D3 | `ubuntu-latest` runner | Faster startup, lower cost; .NET 8 is cross-platform | `windows-latest` (needed only if testing Windows-specific code paths) |
| D4 | Dependabot with grouped updates | Reduces PR noise; groups related packages | Renovate (more flexible but more config) |
| D5 | `dorny/test-reporter` for TRX | Inline test failures on PR checks tab | `EnricoMi/publish-unit-test-result-action` |
| D6 | Self-contained single-file publish | No .NET runtime dependency on target machines | Framework-dependent (smaller, requires runtime) |
| D7 | Semver tags trigger releases | Simple, standard; pre-release detected by hyphen | GitHub Release UI manual trigger |
