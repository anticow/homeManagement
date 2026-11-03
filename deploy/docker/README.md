# Docker Compose — Local Platform Stack

Runs the full HomeManagement platform locally for development and testing.

## Services

| Service | Port | Description |
|---|---|---|
| **Web** | [localhost:8080](http://localhost:8080) | Blazor Server UI |
| **Gateway** | [localhost:8081](http://localhost:8081) | YARP reverse proxy (`/api/*` → Broker, `/auth/*` → Auth) |
| **Broker** | [localhost:8082](http://localhost:8082) | Domain API — all business logic |
| **Auth** | [localhost:8083](http://localhost:8083) | JWT token issuance and user management |
| **Agent GW** | [localhost:9444](http://localhost:9444) | gRPC agent gateway |
| **Agent GW control plane** | [localhost:9445](http://localhost:9445) | HTTP/1 capabilities, control API, and health |
| **SQL Server** | localhost:1433 | Database (sa / `HomeManagement_Dev1!`) |
| **Seq** | [localhost:5380](http://localhost:5380) | Structured log viewer |

## Quick Start

```powershell
# From the repo root:
.\start-platform.ps1

# Detached mode (no log tailing):
.\start-platform.ps1 -Detach

# Without auto-opening browser:
.\start-platform.ps1 -NoBrowser
```

## Manual Commands

```powershell
# Build and start
docker compose -f deploy/docker/docker-compose.yaml up --build -d

# View logs
docker compose -f deploy/docker/docker-compose.yaml logs -f

# Stop
docker compose -f deploy/docker/docker-compose.yaml down

# Stop and remove volumes (fresh start)
docker compose -f deploy/docker/docker-compose.yaml down -v
```

## Startup Order

1. **SQL Server** starts first with health check validation
2. **Seq** starts independently (log aggregation)
3. **Auth** waits for SQL Server to be healthy, then applies EF Core migrations on startup
4. **Agent GW** waits for Seq and exposes its HTTP/1 control plane on port 9445
5. **Broker** waits for SQL Server, Auth, and Agent GW to be healthy, then applies migrations and verifies gateway capabilities
6. **Web** and **Gateway** wait for Broker and Auth to be healthy

Broker uses `http://agent-gw:9445` on the Compose service network, not container-local
`localhost:9444` or the gRPC listener. Both client and gateway retain their default
legacy enrollment mode. Even legacy startup requires authenticated capabilities;
unreachable or incompatible authority must still fail closed. Managed enrollment
requires a separately configured HTTPS control plane and is not enabled by this
development-only stack.

## Dev Credentials

| Secret | Value |
|---|---|
| SA Password | `HomeManagement_Dev1!` |
| JWT Signing Key | `dev-signing-key-at-least-32-characters-long!` |
| Agent GW API Key | `dev-agent-gateway-api-key` |

> These are development-only values. Production secrets are managed via Ansible Vault.

## Security Note — `.env` is for development only

The `.env` file stores secrets as plaintext on disk. This is intentional for local
development but **must not** be used in production.

| Environment | Secret source |
|---|---|
| Local dev | `.env` file (git-ignored) |
| Production | Ansible Vault → Kubernetes Secrets → Helm values |
| Docker Swarm | `docker-compose.swarm-secrets.yaml` overlay (see that file for instructions) |

The `.env.example` template contains placeholder values only. Copy it to `.env` and
fill in your local dev values — never commit a populated `.env` file.
