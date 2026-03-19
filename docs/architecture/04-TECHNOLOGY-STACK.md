# Recommended Technologies and Libraries

## 1. Core Platform

| Component | Choice | Version | Rationale |
|---|---|---|---|
| **Runtime** | .NET 8+ (LTS) | 8.0+ | Cross-platform, AOT-capable, mature ecosystem, single-file publish |
| **Language** | C# 12+ | Latest | Pattern matching, records, nullable references, strong typing |
| **Build** | dotnet CLI + MSBuild | — | Standard toolchain, CI-friendly |
| **Package Manager** | NuGet | — | Standard .NET package ecosystem |

## 2. GUI Framework

| Component | Choice | Rationale |
|---|---|---|
| **UI Framework** | [Avalonia UI](https://avaloniaui.net/) 11+ | True cross-platform (Win/Linux/macOS), XAML-based, active community |
| **MVVM Framework** | [ReactiveUI](https://www.reactiveui.net/) 20+ | Reactive extensions for UI, mature, first-class Avalonia support |
| **Icons** | [Material.Icons.Avalonia](https://github.com/SKProCH/Material.Icons.Avalonia) | Material Design icon set, easy integration |
| **Charts/Graphs** | [LiveChartsCore](https://lvcharts.com/) | Cross-platform charting for dashboards |
| **DataGrid** | Avalonia.Controls.DataGrid | Built-in, performant, sortable/filterable |

## 3. Data Storage

| Component | Choice | Rationale |
|---|---|---|
| **Primary Database** | [SQLite](https://www.sqlite.org/) via [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) | Zero-config, embedded, cross-platform, battle-tested |
| **ORM** | [Entity Framework Core 8+](https://learn.microsoft.com/en-us/ef/core/) with SQLite provider | Code-first migrations, LINQ queries, mature |
| **Encrypted DB** | [SQLCipher](https://www.zetetic.net/sqlcipher/) (optional) | Transparent AES-256 encryption of entire DB file |
| **Migrations** | EF Core Migrations | Schema evolution tracked in code |

## 4. Remote Execution

| Component | Choice | NuGet Package | Rationale |
|---|---|---|---|
| **SSH** | [SSH.NET](https://github.com/sshnet/SSH.NET) | `SSH.NET` | Pure .NET SSH client, no native deps, maintained |
| **WinRM** | [WSManAutomation](https://learn.microsoft.com/en-us/dotnet/api/system.management.automation) / custom WS-Management client | `System.Management.Automation` | PowerShell SDK for WinRM/PS Remoting |
| **PowerShell Hosting** | [PowerShell SDK](https://www.nuget.org/packages/Microsoft.PowerShell.SDK/) | `Microsoft.PowerShell.SDK` | Run PS scripts in-process or remotely |
| **gRPC** | [Grpc.Net](https://grpc.io/docs/languages/csharp/) | `Grpc.AspNetCore`, `Grpc.Net.Client` | Agent ↔ Control communication |

## 5. Security & Cryptography

| Component | Choice | Rationale |
|---|---|---|
| **Encryption** | AES-256-GCM via `System.Security.Cryptography` | Built-in .NET, authenticated encryption |
| **Key Derivation** | [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography) (Argon2id) | Memory-hard KDF, resistant to GPU/ASIC attacks |
| **OS Secret Store (Win)** | DPAPI via `System.Security.Cryptography.ProtectedData` | Windows-native, user-scoped encryption |
| **OS Secret Store (Linux)** | [libsecret](https://wiki.gnome.org/Projects/Libsecret) via P/Invoke or DBus | GNOME Keyring / KDE Wallet integration |
| **Certificate Management** | `System.Security.Cryptography.X509Certificates` | mTLS certificates for agent communication |
| **Secure Memory** | Custom `SecureBuffer` (pinned + zero-on-dispose) | Prevent credential leakage in memory |

## 6. Scheduling

| Component | Choice | Rationale |
|---|---|---|
| **Job Scheduler** | [Quartz.NET](https://www.quartz-scheduler.net/) 3.x | Mature, cron support, persistent job store (SQLite), misfire handling |
| **In-Process Queue** | `System.Threading.Channels` | High-perf, bounded producer-consumer for job execution pipeline |

## 7. Logging & Observability

| Component | Choice | Rationale |
|---|---|---|
| **Structured Logging** | [Serilog](https://serilog.net/) 3.x | Structured JSON logging, rich sink ecosystem |
| **Console Sink** | `Serilog.Sinks.Console` | Dev/debug output |
| **File Sink** | `Serilog.Sinks.File` | Rolling file logs with retention |
| **SQLite Sink** | Custom sink → audit event table | Queryable audit log via GUI |
| **Enrichers** | `Serilog.Enrichers.Thread`, `.Environment`, `.CorrelationId` | Contextual metadata on every log entry |
| **Correlation** | Custom `CorrelationContext` (AsyncLocal) | Trace a user action through all layers |

## 8. Testing

| Component | Choice | Rationale |
|---|---|---|
| **Test Framework** | [xUnit](https://xunit.net/) 2.x | Widely adopted, parallel test execution |
| **Mocking** | [NSubstitute](https://nsubstitute.github.io/) 5.x | Clean syntax, interface-focused mocking |
| **Assertions** | [FluentAssertions](https://fluentassertions.com/) 6.x | Readable assertions, rich diagnostics |
| **Integration Tests** | [Testcontainers](https://dotnet.testcontainers.org/) | Spin up SSH/WinRM targets in Docker for transport tests |
| **Snapshot Testing** | [Verify](https://github.com/VerifyTests/Verify) | UI and data structure snapshot testing |
| **Code Coverage** | [Coverlet](https://github.com/coverlet-coverage/coverlet) | Cross-platform coverage collection |

## 9. Serialization & Configuration

| Component | Choice | Rationale |
|---|---|---|
| **JSON** | `System.Text.Json` | Built-in, fast, AOT-compatible |
| **Configuration** | `Microsoft.Extensions.Configuration` | Layered config (appsettings, env vars, CLI args) |
| **DI Container** | `Microsoft.Extensions.DependencyInjection` | Standard .NET DI, well-integrated |
| **Options Pattern** | `Microsoft.Extensions.Options` | Strongly-typed configuration sections |

## 10. Networking & Discovery

| Component | Choice | Rationale |
|---|---|---|
| **Network Scanning** | Custom ICMP + TCP port probe via `System.Net.NetworkInformation` | Lightweight machine discovery |
| **DNS Resolution** | `System.Net.Dns` | Hostname ↔ IP resolution |

## 11. Development Tooling

| Tool | Purpose |
|---|---|
| **EditorConfig** | Consistent code style across team |
| **Central Package Management** | `Directory.Packages.props` for version alignment |
| **Analyzers** | `Microsoft.CodeAnalysis.NetAnalyzers`, `SonarAnalyzer.CSharp` |
| **Formatting** | `dotnet format` |
| **Git Hooks** | `husky.net` for pre-commit formatting/linting |

---

## NuGet Package Summary

```xml
<!-- Core -->
<PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="8.*" />
<PackageVersion Include="Microsoft.Extensions.Configuration" Version="8.*" />
<PackageVersion Include="Microsoft.Extensions.Options" Version="8.*" />
<PackageVersion Include="Microsoft.Extensions.Logging" Version="8.*" />

<!-- Data -->
<PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.*" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="8.*" />

<!-- Remote Execution -->
<PackageVersion Include="SSH.NET" Version="2024.*" />
<PackageVersion Include="Microsoft.PowerShell.SDK" Version="7.*" />
<PackageVersion Include="Grpc.AspNetCore" Version="2.*" />
<PackageVersion Include="Grpc.Net.Client" Version="2.*" />
<PackageVersion Include="Google.Protobuf" Version="3.*" />
<PackageVersion Include="Grpc.Tools" Version="2.*" />

<!-- Security -->
<PackageVersion Include="Konscious.Security.Cryptography.Argon2" Version="1.*" />

<!-- Scheduling -->
<PackageVersion Include="Quartz" Version="3.*" />
<PackageVersion Include="Quartz.Serialization.SystemTextJson" Version="3.*" />

<!-- Logging -->
<PackageVersion Include="Serilog" Version="3.*" />
<PackageVersion Include="Serilog.Extensions.Logging" Version="8.*" />
<PackageVersion Include="Serilog.Sinks.Console" Version="5.*" />
<PackageVersion Include="Serilog.Sinks.File" Version="5.*" />
<PackageVersion Include="Serilog.Enrichers.Thread" Version="3.*" />
<PackageVersion Include="Serilog.Enrichers.Environment" Version="2.*" />

<!-- GUI -->
<PackageVersion Include="Avalonia" Version="11.*" />
<PackageVersion Include="Avalonia.Desktop" Version="11.*" />
<PackageVersion Include="Avalonia.Themes.Fluent" Version="11.*" />
<PackageVersion Include="Avalonia.ReactiveUI" Version="11.*" />
<PackageVersion Include="Material.Icons.Avalonia" Version="2.*" />
<PackageVersion Include="LiveChartsCore.SkiaSharpView.Avalonia" Version="2.*" />

<!-- Testing -->
<PackageVersion Include="xunit" Version="2.*" />
<PackageVersion Include="xunit.runner.visualstudio" Version="2.*" />
<PackageVersion Include="NSubstitute" Version="5.*" />
<PackageVersion Include="FluentAssertions" Version="6.*" />
<PackageVersion Include="Testcontainers" Version="3.*" />
<PackageVersion Include="coverlet.collector" Version="6.*" />
```
