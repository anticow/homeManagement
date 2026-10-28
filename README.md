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

## Per-node Grafana graphs (web console)

System history is collected by Prometheus from `node_exporter` (port 9100) and
`windows_exporter` (port 9182), not by polling the HomeManagement agent. The
companion Ansible monitoring role scrapes configured endpoints every 30 seconds,
retains data for 15 days by default, and separately scrapes application `/metrics`
endpoints through a ServiceMonitor. The Broker queries Prometheus for the current
machine status; agent heartbeats describe agent health, not whole-host history.
Production ArgoCD values and the companion production inventory enable Broker
Prometheus queries; reusable chart/role defaults remain opt-in for other environments.

The machine detail page offers CPU, memory, per-filesystem disk, and aggregate
network graphs, with 1-hour, 3-hour, 24-hour, and 7-day ranges. Choose **Load graphs**
to start rendering; **Pause graphs** removes the frames and their refresh traffic.
**Refresh status** fetches a new Broker snapshot independently. CPU/network rates
are five-minute averages, not instantaneous readings.

Deploy the companion monitoring role first with
`monitoring_grafana_allow_embedding: true`; it provisions the `hm-node` dashboard.
Then deploy the web app with `Grafana:BaseUrl` (Helm `grafana.baseUrl`).
`Grafana:DashboardUid` defaults to `hm-node` and `Grafana:ScrapeJob` must match
the Prometheus scrape job (`homemanagement` by default).
The instance defaults to `{fqdn}:{port}`, falling back to hostname when FQDN is
absent. For IP targets, custom ports, or different DNS names, set the machine tag
`prometheus:instance` to the **exact** Prometheus instance label, or configure
`Grafana:InstanceLabelTemplate`. Missing targets display no data, not another node.
Compose uses the application's default template; set `Grafana__InstanceLabelTemplate`
directly in a Compose override when a different template is needed.
The dashboard includes macOS node_exporter memory queries; unsupported metrics
remain absent rather than being reported as zero.

Viewers must sign in to Grafana using **Open node dashboard**. Embedding still
requires browser access to Grafana and a working Grafana session; third-party
cookie restrictions can prevent cross-site embedding. Use the full-dashboard
link in that case. No anonymous access is enabled and no service-account token
is placed in HTML or URLs. The old community-dashboard panel settings and
`Grafana:ViewerToken`/`grafana.viewerTokenSecret` configuration are superseded.
After rollout, revoke the previously issued `homemanagement-embed` token and
remove the unused `grafana-viewer-token` secret through the approved operations
path; this change does not revoke or delete live credentials automatically.

## License

Private — All rights reserved.
