# HomeManagement

A cross-platform patching and service-controller system for Windows and Linux machines. Manage your entire fleet from a single desktop application — scan for patches, apply updates, control services, and maintain a complete audit trail.

## Features

- **Machine Inventory** — Register and organize machines by OS, role, tags, and location
- **Patch Management** — Detect, approve, and apply OS patches across Windows and Linux
- **Service Controller** — Start, stop, restart, and monitor system services remotely
- **Encrypted Credential Vault** — AES-256-GCM encrypted storage with Argon2id key derivation
- **Remote Execution** — SSH (Linux), WinRM/PowerShell Remoting (Windows), or optional agent
- **Job Scheduling** — Cron-based recurring jobs for patch scans and maintenance windows
- **Audit Trail** — Tamper-evident, HMAC-chained audit log of every action
- **Cross-Platform GUI** — Avalonia-based desktop app runs on Windows, Linux, and macOS

## Architecture

```
GUI (Avalonia) → Core Engine → Modules → Transport Layer → Remote Machines
                                  ↕
                            SQLite + Vault
```

See [docs/architecture/](docs/architecture/) for the full system design:

| Document | Contents |
|---|---|
| [01 — High-Level Architecture](docs/architecture/01-HIGH-LEVEL-ARCHITECTURE.md) | System overview, diagrams, principles, tech stack |
| [02 — Module Descriptions](docs/architecture/02-MODULE-DESCRIPTIONS.md) | Detailed module specs with interfaces |
| [03 — Data Flows & Interfaces](docs/architecture/03-DATA-FLOWS-AND-INTERFACES.md) | Sequence diagrams, complete interface registry |
| [04 — Technology Stack](docs/architecture/04-TECHNOLOGY-STACK.md) | Libraries, NuGet packages, tooling |
| [05 — Security, Deployment & Scaling](docs/architecture/05-SECURITY-DEPLOYMENT-SCALING.md) | Threat model, retry strategy, observability, roadmap |
| [06 — Project Scaffolding](docs/architecture/06-PROJECT-SCAFFOLDING.md) | Solution layout and dependency graph |

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later

## Quick Start

```bash
# Clone
git clone <repo-url> && cd homeManagement

# Build
dotnet build

# Run the GUI
dotnet run --project src/HomeManagement.Gui

# Run the agent (on remote machines)
dotnet run --project src/HomeManagement.Agent
```

## Project Structure

```
src/
  HomeManagement.Abstractions   Core interfaces, DTOs, enums
  HomeManagement.Core           DI composition root
  HomeManagement.Data           EF Core + SQLite
  HomeManagement.Vault          Encrypted credential storage
  HomeManagement.Transport      SSH, WinRM, PS Remoting, Agent
  HomeManagement.Patching       Patch detection & application
  HomeManagement.Services       Service controller
  HomeManagement.Inventory      Machine inventory
  HomeManagement.Auditing       Audit logging
  HomeManagement.Orchestration  Job scheduling
  HomeManagement.Agent          Lightweight remote agent
  HomeManagement.Gui            Avalonia desktop app
```

## License

Private — All rights reserved.
