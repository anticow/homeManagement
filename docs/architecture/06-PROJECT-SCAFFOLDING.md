# Project Scaffolding & Solution Structure

## Solution Layout

```
HomeManagement.sln
│
├── src/
│   ├── HomeManagement.Abstractions/       Interfaces, DTOs, validated types, cross-cutting
│   │     ├── Interfaces/                  Module contracts (IRemoteExecutor, IPatchService, etc.)
│   │     ├── Models/                      Per-domain model files (not one mega-file)
│   │     ├── Validation/                  Validated value types (Hostname, ServiceName, CidrRange)
│   │     ├── CrossCutting/                ICorrelationContext, ISensitiveDataFilter, IModuleRegistration
│   │     └── Repositories/                Repository interfaces (IMachineRepository, etc.)
│   │
│   ├── HomeManagement.Core/               Composition root, logging bootstrap, module discovery
│   ├── HomeManagement.Data/               EF Core DbContext, entities, repository implementations
│   ├── HomeManagement.Vault/              Encrypted credential vault
│   ├── HomeManagement.Transport/          SSH, WinRM, PS Remoting, Agent gateway
│   ├── HomeManagement.Patching/           Patch detection and application
│   ├── HomeManagement.Services/           Service controller
│   ├── HomeManagement.Inventory/          Machine inventory and metadata
│   ├── HomeManagement.Auditing/           Audit logging and HMAC event store
│   ├── HomeManagement.Orchestration/      Job scheduling and execution
│   ├── HomeManagement.Agent/              Lightweight remote agent (standalone deploy)
│   └── HomeManagement.Gui/               Avalonia desktop application
│
├── tests/                                 (mirror of src/)
├── docs/architecture/                     Architecture documentation
├── Directory.Build.props                  Shared MSBuild properties
├── Directory.Packages.props               Central package management
├── .editorconfig                          Code style rules
└── .gitignore
```

## Dependency Graph (v1.1 — refined)

```
HomeManagement.Gui (host process)
  ├── HomeManagement.Core (composition root, module discovery)
  │     ├── HomeManagement.Abstractions
  │     └── HomeManagement.Data
  │
  │  Module assemblies loaded into GUI process for IModuleRegistration discovery:
  ├── HomeManagement.Vault          → Abstractions only
  ├── HomeManagement.Transport      → Abstractions only
  ├── HomeManagement.Patching       → Abstractions only
  ├── HomeManagement.Services       → Abstractions only
  ├── HomeManagement.Inventory      → Abstractions only
  ├── HomeManagement.Auditing       → Abstractions only
  └── HomeManagement.Orchestration  → Abstractions only

HomeManagement.Data (infra, NOT referenced by business modules)
  └── HomeManagement.Abstractions

HomeManagement.Agent (standalone process)
  └── HomeManagement.Abstractions
```

## Key Rules

1. **Business modules depend ONLY on Abstractions.** No module references Data, Core, or another module.
2. **Data access via repository interfaces.** IMachineRepository, IPatchHistoryRepository, etc. live in Abstractions. Implementations live in Data.
3. **Modules self-register via IModuleRegistration.** Core discovers and invokes them — no hardcoded registrations.
4. **GUI references module assemblies** to ensure they're loaded into the AppDomain for discovery.
5. **Input validated at construction.** Hostname, ServiceName, CidrRange are parsed-or-fail value types.
