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
3. **Broker** and **Auth** wait for SQL Server to be healthy, then apply EF Core migrations on startup
4. **Web** and **Gateway** wait for Broker and Auth to be healthy
5. **Agent GW** starts independently

## Dev Credentials

| Secret | Value |
|---|---|
| SA Password | `HomeManagement_Dev1!` |
| JWT Signing Key | `dev-signing-key-at-least-32-characters-long!` |
| Agent GW API Key | `dev-agent-gateway-api-key` |

> These are development-only values. Production secrets are managed via Ansible Vault.
