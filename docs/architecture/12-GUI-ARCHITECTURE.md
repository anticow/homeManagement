# 12 — GUI Architecture

> Avalonia 11 + ReactiveUI desktop application for the cross-platform
> patching and service-controller system.
> Supersedes GUI-related fragments in docs 02 and 08.

---

## 1  Overview

The GUI is a single-window Avalonia desktop application that runs on Windows
and Linux. It is the primary user interface for managing machines, patching,
services, jobs, credentials, and audit trails.

```
┌──────────────────────────────────────────────────────────────────┐
│  HomeManagement                                              ─ □ × │
├────────────┬─────────────────────────────────────────────────────┤
│            │                                                     │
│  Dashboard │   ┌─────────────────────────────────────────────┐   │
│  Machines  │   │                                             │   │
│  Patching  │   │          Active Page Content                │   │
│  Services  │   │          (ContentControl bound to           │   │
│  Jobs      │   │           CurrentPage ViewModel)            │   │
│  Credentials│  │                                             │   │
│  Audit Log │   │                                             │   │
│  Settings  │   └─────────────────────────────────────────────┘   │
│            │                                                     │
├────────────┴─────────────────────────────────────────────────────┤
│  ● Connected  │  2 jobs running  │  Vault: unlocked  │  v1.0.0  │
└──────────────────────────────────────────────────────────────────┘
```

### Design principles

| # | Principle | Implementation |
|---|-----------|----------------|
| P1 | **MVVM strict** | Views are XAML-only; all logic lives in ViewModels via ReactiveUI |
| P2 | **Service layer only** | ViewModels never touch EF Core, files, or network — they call Abstractions interfaces |
| P3 | **Reactive first** | `IObservable<T>` for job progress, agent events; `ReactiveCommand` for all user actions |
| P4 | **UI thread safety** | All `ObserveOn(RxApp.MainThreadScheduler)` before binding to properties |
| P5 | **Cancellable operations** | Every long-running command wired to a `CancellationTokenSource` the user can cancel |
| P6 | **Graceful degradation** | Service failures show inline error banners, never crash the app |

---

## 2  GUI Layout and Navigation Structure

### 2.1  Visual hierarchy

```
Window (1280×800, DockPanel)
├── [Left]  NavigationSidebar (220 px, fixed)
│   ├── App logo + title
│   ├── NavItem: Dashboard        (icon: Home)
│   ├── NavItem: Machines         (icon: Server)
│   ├── NavItem: Patching         (icon: Shield)
│   ├── NavItem: Services         (icon: Cog)
│   ├── NavItem: Jobs             (icon: ListChecked)
│   ├── NavItem: Credentials      (icon: Key)
│   ├── NavItem: Audit Log        (icon: FileText)
│   └── NavItem: Settings         (icon: Gear)
│
├── [Bottom]  StatusBar (28 px, fixed)
│   ├── ConnectionIndicator  (●/○ + agent count)
│   ├── RunningJobsBadge     ("3 jobs running")
│   ├── VaultStatusIndicator ("Vault: unlocked/locked")
│   └── AppVersion           ("v1.0.0")
│
└── [Fill]  ContentHost (ContentControl)
    └── DataTemplate-switched view per active ViewModel
```

### 2.2  Navigation model

Navigation uses a `ReactiveObject`-based `NavigationService` that holds
a `CurrentPage` property. The `MainWindowViewModel` binds the sidebar
selection to `NavigationService.NavigateTo<TViewModel>()`.

```
NavigationService
├── CurrentPage : ViewModelBase          (observable property)
├── NavigateTo<T>() where T : ViewModelBase
├── GoBack()                              (optional breadcrumb stack)
└── CanGoBack : bool
```

ViewModels are resolved by the DI container (transient lifetime — fresh state
each navigation unless performance dictates singleton).

**View resolution** uses Avalonia's `ViewLocator` convention:
`FooViewModel` → resolves to `FooView` automatically.

### 2.3  Page inventory

| Page | ViewModel | Primary service dependencies |
|------|-----------|------------------------------|
| Dashboard | `DashboardViewModel` | `ISystemHealthService`, `IJobScheduler`, `IInventoryService`, `IAgentGateway` |
| Machines | `MachinesViewModel` | `IInventoryService`, `IRemoteExecutor` |
| Machine Detail | `MachineDetailViewModel` | `IInventoryService`, `IPatchService`, `IServiceController` |
| Patching | `PatchingViewModel` | `IPatchService`, `IJobScheduler`, `IInventoryService`, `ICommandBroker` |
| Services | `ServicesViewModel` | `IServiceController`, `IInventoryService`, `ICommandBroker` |
| Jobs | `JobsViewModel` | `IJobScheduler` |
| Job Detail | `JobDetailViewModel` | `IJobScheduler` |
| Credentials | `CredentialsViewModel` | `ICredentialVault` |
| Audit Log | `AuditLogViewModel` | `IAuditLogger` |
| Settings | `SettingsViewModel` | `ICredentialVault` (key rotation), application config |
| Vault Unlock | `VaultUnlockViewModel` | `ICredentialVault` |

---

## 3  Internal Architecture

### 3.1  Project structure

```
HomeManagement.Gui/
├── App.axaml / App.axaml.cs              — Application root + DI bootstrap
├── Program.cs                             — Entry point (STA thread)
│
├── ViewModels/
│   ├── ViewModelBase.cs                   — ReactiveObject + shared helpers
│   ├── MainWindowViewModel.cs             — Shell: navigation, status bar bindings
│   ├── DashboardViewModel.cs              — System health, quick stats, recent jobs
│   ├── MachinesViewModel.cs               — Machine list, search, add/remove
│   ├── MachineDetailViewModel.cs          — Single machine: patches, services, metadata
│   ├── PatchingViewModel.cs               — Patch scan/apply across machine selection
│   ├── ServicesViewModel.cs               — Service list/control per machine
│   ├── JobsViewModel.cs                   — Job history, active jobs, scheduling
│   ├── JobDetailViewModel.cs              — Per-machine results, live progress
│   ├── CredentialsViewModel.cs            — Credential CRUD, vault lock/unlock
│   ├── AuditLogViewModel.cs               — Event browser, filtering, export
│   ├── SettingsViewModel.cs               — App config, key rotation
│   └── VaultUnlockViewModel.cs            — Master password entry
│
├── Views/
│   ├── MainWindow.axaml                   — Shell layout (sidebar, content host, status bar)
│   ├── DashboardView.axaml
│   ├── MachinesView.axaml
│   ├── MachineDetailView.axaml
│   ├── PatchingView.axaml
│   ├── ServicesView.axaml
│   ├── JobsView.axaml
│   ├── JobDetailView.axaml
│   ├── CredentialsView.axaml
│   ├── AuditLogView.axaml
│   ├── SettingsView.axaml
│   └── VaultUnlockView.axaml
│
├── Controls/
│   ├── StatusBarControl.axaml             — Bottom status bar (connection, jobs, vault)
│   ├── MachinePickerControl.axaml         — Reusable machine selector (grid + checkboxes)
│   ├── ProgressOverlayControl.axaml       — Modal overlay for long-running ops
│   ├── ErrorBannerControl.axaml           — Inline dismissible error notification
│   └── DataGridFilterControl.axaml        — Column filter row for DataGrids
│
├── Converters/
│   ├── StateToColorConverter.cs           — MachineState/ServiceState → Brush
│   ├── SeverityToIconConverter.cs         — PatchSeverity → icon path
│   ├── BoolToVisibilityConverter.cs       — bool → IsVisible
│   ├── EnumToStringConverter.cs           — Display-friendly enum labels
│   └── TimeAgoConverter.cs                — DateTime → "3 minutes ago"
│
├── Services/
│   ├── NavigationService.cs               — Page routing + back stack
│   ├── DialogService.cs                   — Confirmation/error dialogs (IDialogService)
│   ├── ClipboardService.cs               — Copy + auto-clear timer (IClipboardService)
│   ├── ViewLocator.cs                     — ViewModel → View convention mapper
│   └── IdleTimerService.cs               — Auto-lock vault after inactivity
│
└── Design/
    └── DesignData.cs                      — Design-time ViewModels for XAML previewer
```

### 3.2  Class responsibilities

| Class | Responsibility |
|-------|---------------|
| `ViewModelBase` | Inherits `ReactiveObject`. Provides `IsBusy`, `ErrorMessage` observable properties; `RunSafe(Func<Task>)` helper that sets `IsBusy`/catches exceptions/sets `ErrorMessage`; disposable tracking via `CompositeDisposable`. |
| `MainWindowViewModel` | Owns `NavigationService`, `StatusBarState`, reactive subscriptions to jobs/agents/vault. Top-level error boundary. |
| `NavigationService` | Stores `CurrentPage` (`ViewModelBase`); `NavigateTo<T>()` resolves from DI; optional back stack for drill-down (Machines → MachineDetail). |
| `ViewLocator` | Implements `IDataTemplate`; maps `*ViewModel` → `*View` by naming convention; returns "Not Found" placeholder for unmapped. |
| `DialogService` | Shows modal dialogs for confirmations ("Apply 12 patches?"), errors, and input prompts. Platform-agnostic via Avalonia's window model. |
| `ClipboardService` | Copies text to clipboard; auto-clears after configurable timeout (default 30 s) to prevent credential leaks. |
| `IdleTimerService` | Tracks pointer/keyboard activity via Avalonia's InputManager; locks vault after `IdleTimeoutMinutes` (default 15) of inactivity. |

---

## 4  Backend API Calls

### 4.1  Service interaction pattern

ViewModels interact exclusively with Abstractions interfaces. No ViewModel
ever references EF Core, HTTP clients, or file I/O directly.

```
ViewModel                 Abstractions Interface          Module Implementation
────────                  ────────────────────            ─────────────────────
PatchingViewModel    →    IPatchService.DetectAsync()   → PatchServiceImpl → IRemoteExecutor → SSH/Agent
MachinesViewModel    →    IInventoryService.QueryAsync() → InventoryServiceImpl → DbContext
CredentialsViewModel →    ICredentialVault.ListAsync()   → CredentialVaultService → vault.enc file
```

### 4.2  Call patterns per page

#### Dashboard

```csharp
// On navigation (automatic load)
HealthReport = await _healthService.CheckAsync(ct);
RecentJobs = await _jobScheduler.ListJobsAsync(new JobQuery(PageSize: 5), ct);
MachineCount = (await _inventory.QueryAsync(new MachineQuery(PageSize: 1), ct)).TotalCount;
ConnectedAgents = _agentGateway.GetConnectedAgents();
```

#### Machines

```csharp
// List with search/filter
Machines = await _inventory.QueryAsync(new MachineQuery(SearchText: SearchText, ...), ct);

// Add machine
await _inventory.AddAsync(new MachineCreateRequest(...), ct);

// Test connection
ConnectionResult = await _remoteExecutor.TestConnectionAsync(target, ct);

// Refresh metadata
await _inventory.RefreshMetadataAsync(machineId, ct);

// Navigate to detail
_navigation.NavigateTo<MachineDetailViewModel>(machineId);
```

#### Patching

```csharp
// Detect patches (streamed)
await foreach (var patch in _patchService.DetectStreamAsync(target, ct))
    Patches.Add(patch);

// Apply patches (via job for multi-machine)
var jobId = await _jobScheduler.SubmitAsync(new JobDefinition(
    Name: "Patch Apply",
    Type: JobType.PatchApply,
    TargetMachineIds: selectedIds,
    Parameters: { ["patchIds"] = selectedPatchIds }), ct);
```

#### Services

```csharp
// List services on selected machine
Services = await _serviceController.ListServicesAsync(target, filter, ct);

// Control service (fire-and-forget via ICommandBroker)
var trackingId = await _broker.SubmitAsync(new CommandEnvelope(
    Target: machineTarget,
    Command: BuildControlCommand(serviceName, ServiceAction.Restart)));
StatusMessage = $"Dispatched Restart (tracking: {trackingId:N})";

// Auto-refresh when the async result arrives
_broker.CompletedStream
    .Where(e => e.MachineId == SelectedMachine?.Id)
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(async evt => {
        await RefreshServicesAsync();
        StatusMessage = evt.Result.ExitCode == 0
            ? "Service restarted successfully"
            : $"Failed: {evt.Result.Stderr}";
    });
```

#### Jobs

```csharp
// List with filters
Jobs = await _jobScheduler.ListJobsAsync(new JobQuery(Type: filter, State: stateFilter, Page: page), ct);

// Cancel job
await _jobScheduler.CancelAsync(jobId, ct);

// Live progress (reactive)
_jobScheduler.GetJobProgressStream(jobId)
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(evt => UpdateProgress(evt));
```

#### Credentials

```csharp
// Vault gated — check before every operation
if (!_vault.IsUnlocked)
{
    _navigation.NavigateTo<VaultUnlockViewModel>(returnTo: typeof(CredentialsViewModel));
    return;
}

// CRUD
Credentials = await _vault.ListAsync(ct);
await _vault.AddAsync(new CredentialCreateRequest(...), ct);
await _vault.RemoveAsync(id, ct);
```

#### Audit Log

```csharp
// Paginated query with filters
Events = await _auditLogger.QueryAsync(new AuditQuery(
    Action: actionFilter,
    FromUtc: dateFrom,
    ToUtc: dateTo,
    Page: page), ct);

// Export
await _auditLogger.ExportAsync(query, ExportFormat.Csv, outputStream, ct);
```

### 4.3  Vault gate pattern

Many pages require an unlocked vault (Machines, Patching, Services need
credentials for remote execution). The `ViewModelBase` provides a guard:

```csharp
protected async Task<bool> EnsureVaultUnlockedAsync()
{
    if (_vault.IsUnlocked) return true;
    _navigation.NavigateTo<VaultUnlockViewModel>(returnTo: GetType());
    return false;
}
```

After successful unlock, `VaultUnlockViewModel` navigates back to the
originating page via the `returnTo` parameter.

---

## 5  Event-Driven Updates

### 5.1  Reactive streams

The GUI subscribes to three `IObservable<T>` streams for real-time updates:

```
┌──────────────────────────────────────────────────────────┐
│                    MainWindowViewModel                     │
│                                                          │
│  ┌─ IJobScheduler.ProgressStream ──────────────────────┐ │
│  │  → RunningJobCount (status bar)                     │ │
│  │  → JobDetailViewModel.ProgressPercent (live bar)    │ │
│  └─────────────────────────────────────────────────────┘ │
│                                                          │
│  ┌─ IAgentGateway.ConnectionEvents ────────────────────┐ │
│  │  → ConnectedAgentCount (status bar)                 │ │
│  │  → DashboardViewModel.AgentList (live refresh)      │ │
│  │  → Toast notification on connect/disconnect         │ │
│  └─────────────────────────────────────────────────────┘ │
│                                                          │
│  ┌─ ICredentialVault.IsUnlocked (polled timer) ────────┐ │
│  │  → VaultStatus (status bar: "Locked" / "Unlocked")  │ │
│  │  → Auto-redirect to VaultUnlockView if locked       │ │
│  │    while on credential-dependent page               │ │
│  └─────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

### 5.2  Subscription lifecycle

All subscriptions are created in `MainWindowViewModel` constructor and
disposed in its `Dispose()` (called on window close). Child ViewModels
subscribe to page-specific streams and dispose when navigated away.

```csharp
// In MainWindowViewModel constructor
_jobScheduler.ProgressStream
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(evt =>
    {
        RunningJobCount = CalculateRunningJobs();
        // Forward to active JobDetailViewModel if applicable
        if (CurrentPage is JobDetailViewModel detail && detail.JobId == evt.JobId)
            detail.OnProgress(evt);
    })
    .DisposeWith(Disposables);

_agentGateway.ConnectionEvents
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(evt =>
    {
        ConnectedAgentCount = _agentGateway.GetConnectedAgents().Count;
        // Toast notification
        StatusMessage = evt.Type switch
        {
            AgentConnectionEventType.Connected => $"Agent {evt.Hostname} connected",
            AgentConnectionEventType.Disconnected => $"Agent {evt.Hostname} disconnected",
            _ => StatusMessage
        };
    })
    .DisposeWith(Disposables);
```

### 5.3  Data refresh strategy

| Trigger | Action |
|---------|--------|
| Page navigation | Fresh data load via service call |
| Reactive stream event | Targeted property update (not full reload) |
| Manual refresh button | Full data reload for current page |
| Job completion | Auto-refresh affected pages (Patching after PatchApply, Services after ServiceControl) |
| Timer-based | Health check every 60 s for dashboard; vault lock check every 10 s |

### 5.4  Event routing between ViewModels

When a job completes that affects another page, the `MainWindowViewModel` acts
as event hub:

```
ProgressStream → job completed → job type == PatchApply?
  → if PatchingViewModel is the active page → call RefreshCommand.Execute()

ProgressStream → job completed → job type == ServiceControl?
  → if ServicesViewModel is the active page → call RefreshCommand.Execute()
```

This avoids tight coupling between sibling ViewModels — they only communicate
through the shared event hub in the shell ViewModel.

---

## 6  Logging and Status Display

### 6.1  Status bar architecture

The status bar is a dedicated `StatusBarControl` bound to properties on
`MainWindowViewModel`:

```csharp
public class MainWindowViewModel : ViewModelBase
{
    // Status bar bindings
    [Reactive] public int ConnectedAgentCount { get; set; }
    [Reactive] public int RunningJobCount { get; set; }
    [Reactive] public bool IsVaultUnlocked { get; set; }
    [Reactive] public string StatusMessage { get; set; } = "Ready";
    [Reactive] public string AppVersion { get; set; }
}
```

**Status bar segments:**

| Segment | Binding | Update source |
|---------|---------|---------------|
| Connection indicator | `ConnectedAgentCount` | `IAgentGateway.ConnectionEvents` stream |
| Running jobs | `RunningJobCount` | `IJobScheduler.ProgressStream` |
| Vault status | `IsVaultUnlocked` | Polling timer (10 s) on `ICredentialVault.IsUnlocked` |
| Status message | `StatusMessage` | Transient messages (auto-clear after 5 s) |
| Version | `AppVersion` | Static from assembly version |

### 6.2  In-app logging view

The Audit Log page provides a rich event browser:

```
┌─────────────────────────────────────────────────────────────┐
│  Audit Log                                    [Export ▼]    │
├─────────────────────────────────────────────────────────────┤
│  Filter: [Action ▼] [Outcome ▼] [From: ___] [To: ___] 🔍  │
├─────────────────────────────────────────────────────────────┤
│  Timestamp          │ Action        │ Target  │ Outcome     │
│  2026-03-15 10:30   │ PatchInstall  │ srv-01  │ ✓ Success   │
│  2026-03-15 10:29   │ PatchScan     │ srv-01  │ ✓ Success   │
│  2026-03-15 10:25   │ VaultUnlocked │ —       │ ✓ Success   │
│  ...                │               │         │             │
├─────────────────────────────────────────────────────────────┤
│  ◄ Page 1 of 12  ►                          500 events     │
└─────────────────────────────────────────────────────────────┘
```

### 6.3  Application logging (Serilog)

All application-level logging goes through Serilog (configured in
`LoggingBootstrap`). The GUI adds a correlation ID enricher so every
user-initiated action can be traced end-to-end:

```
User clicks "Apply Patches"
  → CorrelationContext.NewScope("user-action-{guid}")
  → JobScheduler.SubmitAsync(...)  [correlation ID in context]
  → PatchService.ApplyAsync(...)   [same correlation ID]
  → AuditLogger.RecordAsync(...)   [same correlation ID]
  → Transport.ExecuteAsync(...)    [same correlation ID]
```

Log files are written to `{dataDir}/logs/hm-{date}.log` with 30-day
retention. The GUI itself does not expose raw log files in the UI — that is
an admin/debug concern accessed via the file system.

### 6.4  Operation feedback

Every user-initiated operation provides visual feedback:

| Stage | Visual |
|-------|--------|
| Initiated | Button becomes disabled; spinner appears |
| In progress | `ProgressOverlayControl` with message + cancel button |
| Success | Green toast notification (auto-dismiss 3 s) |
| Failure | `ErrorBannerControl` with error message + retry button |

---

## 7  Error Handling

### 7.1  Error handling layers

```
Layer 1: ReactiveCommand error handler (per-command)
  │  Catches exceptions from the async operation
  │  Sets ViewModel.ErrorMessage
  │
Layer 2: ViewModelBase.RunSafe() (convenience wrapper)
  │  try/catch → sets IsBusy = false, ErrorMessage = ex.Message
  │  Classifies errors by ErrorCategory
  │
Layer 3: MainWindowViewModel global handler
  │  RxApp.DefaultExceptionHandler
  │  Catches unhandled reactive pipeline errors
  │  Logs + shows fallback error dialog
  │
Layer 4: AppDomain.UnhandledException
     Last resort — logs critical error and shows crash dialog
```

### 7.2  Error classification and UX

| ErrorCategory | User-visible behavior |
|---------------|-----------------------|
| `Transient` | Inline warning banner with automatic retry option |
| `Authentication` | Redirect to vault unlock page |
| `Authorization` | Inline error: "Insufficient permissions on {machine}" |
| `TargetError` | Inline error with machine name + stderr excerpt |
| `ConfigurationError` | Inline error with "Check settings" link |
| `SystemError` | Error dialog with "View logs" link |

### 7.3  Vault-locked error handling

If a service call fails because the vault is locked (credential retrieval
fails), the error handler:

1. Catches the vault-locked exception
2. Saves the current navigation state
3. Navigates to `VaultUnlockViewModel` with `returnTo` parameter
4. On successful unlock, navigates back and retries the operation

### 7.4  Connection failure handling

When remote operations fail due to connectivity:

```csharp
// In ViewModelBase
catch (Exception ex) when (ClassifyError(ex) == ErrorCategory.Transient)
{
    ErrorMessage = $"Connection failed: {ex.Message}";
    CanRetry = true;  // Shows retry button in ErrorBannerControl
}
```

### 7.5  Bulk operation failure handling

For multi-machine operations (bulk patch apply, bulk service control):

1. The `JobDetailViewModel` shows per-machine results in a table
2. Failed machines are highlighted in red with error messages
3. A "Retry Failed" button re-submits the job for only the failed machines
4. The original job's audit trail links to the retry job via correlation ID

---

## 8  Threading Model

### 8.1  Thread architecture

```
Main / UI Thread (STA)
├── Avalonia rendering + input events
├── ViewModel property changes (INotifyPropertyChanged)
├── ReactiveCommand subscriptions (ObserveOn MainThreadScheduler)
└── Navigation, dialog display

Thread Pool (Task.Run / ConfigureAwait(false))
├── All service interface calls (I/O-bound)
│   ├── EF Core queries (via DbContext)
│   ├── SSH / WinRM command execution
│   ├── gRPC communication (agent gateway)
│   └── File I/O (vault, exports)
├── Patch detection (potentially long-running)
└── Metadata refresh (per-machine SSH calls)

Dedicated Timer Threads
├── Health check timer (60 s)
├── Vault lock check timer (10 s)
└── Idle detection timer (1 s resolution)
```

### 8.2  ReactiveCommand pattern

Every user-triggered action uses `ReactiveCommand.CreateFromTask()`,
which automatically:
- Executes the async body on the thread pool
- Marshals the result back to the UI thread
- Tracks `IsExecuting` (bound to button disabled state / spinner)
- Routes exceptions to `ThrownExceptions` observable

```csharp
// Typical ViewModel command setup
RefreshCommand = ReactiveCommand.CreateFromTask(async ct =>
{
    var result = await _inventory.QueryAsync(query, ct);
    return result;
});

RefreshCommand
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(result => Machines = result.Items);

RefreshCommand.ThrownExceptions
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(ex => ErrorMessage = ex.Message);

RefreshCommand.IsExecuting
    .ToPropertyEx(this, x => x.IsBusy);
```

### 8.3  Cancellation

Every long-running command supports cancellation:

```csharp
// ReactiveCommand natively supports CancellationToken
ApplyPatchesCommand = ReactiveCommand.CreateFromTask(
    async ct =>
    {
        // ct is automatically cancelled if:
        // 1. User clicks Cancel (via command.Dispose())
        // 2. ViewModel is disposed (navigation away)
        var jobId = await _jobScheduler.SubmitAsync(definition, ct);
        return jobId;
    },
    canExecute: this.WhenAnyValue(x => x.SelectedMachines.Count, c => c > 0));

// Explicit cancel via separate command
CancelCommand = ReactiveCommand.Create(() =>
{
    _applyCts?.Cancel();
});
```

### 8.4  Collection binding (thread safety)

Avalonia `DataGrid` and `ListBox` require collection changes on the UI thread.
We use two patterns:

**Pattern A — Full replacement (small datasets):**
```csharp
// Service call on thread pool, replace collection on UI thread
var result = await _service.QueryAsync(query, ct);
// ReactiveCommand automatically marshals to UI thread
Machines = new ObservableCollection<Machine>(result.Items);
```

**Pattern B — Incremental append (streaming):**
```csharp
// For IAsyncEnumerable / IObservable streaming results
await foreach (var patch in _patchService.DetectStreamAsync(target, ct))
{
    // Must dispatch to UI thread for collection mutation
    await Dispatcher.UIThread.InvokeAsync(() => Patches.Add(patch));
}
```

### 8.5  Preventing UI freezes

| Risk | Mitigation |
|------|-----------|
| Large DataGrid rendering | Virtualized panels (Avalonia default); paginate at 50–100 items |
| Slow service call on navigation | Show skeleton/loading state immediately; populate asynchronously |
| Blocking `MainThread` | Never `Task.Result` or `.Wait()` — always `await` |
| Heavy serialization | Use `Task.Run()` for JSON serialization of large datasets |
| Multiple rapid clicks | `ReactiveCommand.IsExecuting` auto-disables button during execution |

---

## 9  Application Bootstrap

### 9.1  Startup sequence

```
Program.Main()
  │
  ├──[1] Build AppBuilder (Avalonia, ReactiveUI, platform detect)
  │
  └──[2] App.OnFrameworkInitializationCompleted()
         │
         ├──[3] Build ServiceCollection
         │      ├── AddHomeManagementLogging(dataDir)   → Serilog
         │      ├── AddHomeManagement(dataDir)           → DbContext, modules
         │      ├── AddSingleton<NavigationService>
         │      ├── AddSingleton<IDialogService, DialogService>
         │      ├── AddSingleton<IClipboardService, ClipboardService>
         │      ├── AddSingleton<IdleTimerService>
         │      ├── AddTransient<DashboardViewModel>
         │      ├── AddTransient<MachinesViewModel>
         │      ├── AddTransient<...all ViewModels>
         │      └── AddSingleton<MainWindowViewModel>
         │
         ├──[4] BuildServiceProvider()
         │
         ├──[5] InitializeDatabaseAsync()   → EF Core migrations
         │
         ├──[6] Resolve MainWindowViewModel
         │      └── Constructor subscribes to reactive streams
         │
         ├──[7] Create MainWindow { DataContext = mainVM }
         │
         ├──[8] Navigate to DashboardViewModel (default page)
         │
         └──[9] Show window
```

### 9.2  Data directory

```
Windows: %LOCALAPPDATA%\HomeManagement\
Linux:   ~/.local/share/homemanagement/

├── homemanagement.db     — SQLite database
├── vault.enc             — Encrypted credential vault
└── logs/
    └── hm-{date}.log    — Application logs
```

---

## 10  Page Designs

### 10.1  Dashboard

```
┌──────────────────────────────────────────────────────────┐
│  Dashboard                                               │
├──────────┬──────────┬──────────┬────────────────────────┤
│ Machines │ Patches  │  Jobs    │  System Health         │
│   12     │  3 crit  │  2 run   │  ● Healthy             │
│  online  │  pending │  1 sched │  DB: ✓  Vault: ✓       │
├──────────┴──────────┴──────────┤  Transport: ✓          │
│                                 │  Agents: 3/3           │
│  Recent Activity               ├────────────────────────┤
│  ┌──────────────────────────┐  │  Connected Agents      │
│  │ 10:30 Patch scan srv-01  │  │  ┌──────────────────┐  │
│  │ 10:25 Service restart    │  │  │ server-01  v1.2  │  │
│  │ 10:20 Agent connected    │  │  │ server-02  v1.2  │  │
│  │ 10:15 Vault unlocked     │  │  │ pi-node    v1.1  │  │
│  └──────────────────────────┘  │  └──────────────────┘  │
└─────────────────────────────────┴────────────────────────┘
```

### 10.2  Machines

```
┌──────────────────────────────────────────────────────────┐
│  Machines                         [+ Add] [⟳ Refresh]   │
├──────────────────────────────────────────────────────────┤
│  🔍 Search: [___________]  OS: [All ▼]  State: [All ▼]  │
├──────────────────────────────────────────────────────────┤
│  Hostname       │ OS      │ State   │ Mode     │ Last   │
│  ──────────────────────────────────────────────────────  │
│  web-server-01  │ Linux   │ ● Online │ Agent   │ 2m ago │
│  db-server-01   │ Linux   │ ● Online │ SSH     │ 5m ago │
│  win-desktop    │ Windows │ ○ Offline│ WinRM   │ 1d ago │
│  ...            │         │         │          │        │
├──────────────────────────────────────────────────────────┤
│  ◄ 1 of 3 ►                               12 machines   │
└──────────────────────────────────────────────────────────┘
```

### 10.3  Patching

```
┌──────────────────────────────────────────────────────────┐
│  Patching                                                │
├────────────────────┬─────────────────────────────────────┤
│  Machine Selection │  Detected Patches                   │
│  ☑ web-server-01   │  ┌─ KB5012345 ──────────────────┐  │
│  ☑ db-server-01    │  │ Security Update for .NET 8    │  │
│  ☐ win-desktop     │  │ Severity: ● Critical          │  │
│                    │  │ Size: 45 MB  Reboot: No       │  │
│  [Select All]      │  └──────────────────────────────┘  │
│  [Scan Selected]   │  ┌─ KB5012346 ──────────────────┐  │
│                    │  │ Cumulative Update 2026-03     │  │
│                    │  │ Severity: ● Important         │  │
│                    │  │ Size: 120 MB  Reboot: Yes     │  │
│                    │  └──────────────────────────────┘  │
│                    │                                     │
│                    │  [☑ Select All] [Apply Selected]    │
└────────────────────┴─────────────────────────────────────┘
```

### 10.4  Services

```
┌──────────────────────────────────────────────────────────┐
│  Services                                                │
├──────────────────────────────────────────────────────────┤
│  Machine: [web-server-01 ▼]   Filter: [________] 🔍     │
├──────────────────────────────────────────────────────────┤
│  Service        │ State    │ Startup   │ PID   │Actions │
│  ─────────────────────────────────────────────────────── │
│  nginx          │ ● Running│ Automatic │ 1234  │ ⏹ ↻   │
│  postgresql     │ ● Running│ Automatic │ 5678  │ ⏹ ↻   │
│  cron           │ ● Running│ Automatic │ 901   │ ⏹ ↻   │
│  bluetooth      │ ○ Stopped│ Disabled  │ —     │ ▶ ↻   │
│  ...            │          │           │       │        │
├──────────────────────────────────────────────────────────┤
│  Showing 24 of 142 services              [Show All]     │
└──────────────────────────────────────────────────────────┘
```

### 10.5  Jobs

```
┌──────────────────────────────────────────────────────────┐
│  Jobs                              [+ New Job] [Schedules] │
├──────────────────────────────────────────────────────────┤
│  Type: [All ▼]  State: [All ▼]  [From: ___] [To: ___]   │
├──────────────────────────────────────────────────────────┤
│  Name            │ Type      │ State     │ Progress      │
│  ────────────────────────────────────────────────────────│
│  Patch Apply     │ PatchApply│ ● Running │ ████░░ 67%   │
│  Service Restart │ SvcCtrl   │ ✓ Done    │ ██████ 100%  │
│  Weekly Scan     │ PatchScan │ ◷ Queued  │ ░░░░░░ 0%    │
│  ...             │           │           │               │
├──────────────────────────────────────────────────────────┤
│  ◄ 1 of 8 ►                            38 jobs          │
└──────────────────────────────────────────────────────────┘
```

---

## 11  Security Considerations

| Concern | Mitigation |
|---------|-----------|
| **Credentials in memory** | `SecureString` for master password; `CredentialPayload` is `IDisposable` → zeroed immediately after use; never stored in ViewModel properties |
| **Clipboard leaks** | `ClipboardService` auto-clears after 30 s (configurable) |
| **Unattended sessions** | `IdleTimerService` tracks InputManager events; locks vault after 15 min (configurable); navigates to `VaultUnlockView` |
| **UI-visible secrets** | Password fields use `PasswordChar="•"`; masked in all views; no credential values in DataGrid columns |
| **Correlation tracking** | Every user action starts a correlation scope → full audit trail from click to remote execution |
| **Error message leaks** | Server-side error messages are sanitized; stack traces shown only in log files, never in UI |

---

## 12  Reusable Controls

### 12.1  MachinePickerControl

A reusable DataGrid with checkboxes for selecting target machines. Used in
Patching, Services (bulk), and Job creation.

```
Properties:
  SelectedMachines : IReadOnlyList<MachineTarget>  (output)
  MachineFilter    : MachineQuery                   (input, optional)
  SelectionMode    : Single | Multiple               (default: Multiple)
```

### 12.2  ProgressOverlayControl

Semi-transparent overlay shown during long-running operations.

```
Properties:
  IsVisible   : bool        (bound to command.IsExecuting)
  Message     : string      ("Scanning 3 machines for patches...")
  Progress    : double?     (null = indeterminate spinner; 0–100 = progress bar)
  CancelCommand : ICommand  (optional Cancel button)
```

### 12.3  ErrorBannerControl

Dismissible inline error notification at the top of page content.

```
Properties:
  Message     : string
  IsVisible   : bool        (bound to ErrorMessage != null)
  CanRetry    : bool        (shows Retry button)
  RetryCommand: ICommand
  DismissCommand: ICommand  (clears ErrorMessage)
```

### 12.4  StatusBarControl

Bottom status bar with segmented indicators.

```
Properties:
  ConnectedAgentCount : int
  RunningJobCount     : int
  IsVaultUnlocked     : bool
  StatusMessage       : string
  AppVersion          : string
```

---

## 13  Converters

| Converter | Input → Output |
|-----------|----------------|
| `StateToColorConverter` | `MachineState.Online` → `#4CAF50` green; `Offline` → `#F44336` red; `Unreachable` → `#FF9800` orange; `Maintenance` → `#2196F3` blue. Same pattern for `ServiceState`, `JobState`. |
| `SeverityToIconConverter` | `PatchSeverity.Critical` → filled red shield; `Important` → orange shield; `Moderate` → yellow; `Low` → grey; `Unclassified` → outline. |
| `BoolToVisibilityConverter` | `true` → `IsVisible=true`; `false` → `IsVisible=false`. Parameter `"invert"` reverses. |
| `EnumToStringConverter` | Converts enum values to display-friendly strings (e.g., `PatchApply` → `"Patch Apply"`). |
| `TimeAgoConverter` | `DateTime` → human-readable relative time: `"3 minutes ago"`, `"2 hours ago"`, `"yesterday"`. |

---

## 14  View Locator Convention

The `ViewLocator` implements `IDataTemplate` and maps ViewModels to Views
by naming convention:

```csharp
public class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data is null) return new TextBlock { Text = "No content" };

        var vmType = data.GetType();
        var viewTypeName = vmType.FullName!.Replace("ViewModel", "View");
        var viewType = Type.GetType(viewTypeName);

        if (viewType is not null)
            return (Control)Activator.CreateInstance(viewType)!;

        return new TextBlock { Text = $"View not found: {viewTypeName}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
```

Registration in `App.axaml`:
```xml
<Application.DataTemplates>
    <local:ViewLocator />
</Application.DataTemplates>
```

---

## 15  Performance Targets

| Metric | Target |
|--------|--------|
| Window launch to content visible | < 2 s |
| Page navigation (cold) | < 500 ms |
| Page navigation (warm, cached VM) | < 100 ms |
| DataGrid with 100 rows | < 200 ms render |
| DataGrid with 1000 rows (virtualized) | < 500 ms initial; smooth scroll |
| Status bar update latency | < 100 ms from event to display |
| Memory (idle, dashboard visible) | < 150 MB |
| Memory (large machine list, 500 machines) | < 250 MB |

---

## 16  Testing Strategy

| Layer | What | How |
|-------|------|-----|
| **ViewModel unit tests** | Command logic, property changes, error handling, navigation calls | xUnit + ReactiveUI test scheduler; mock all service interfaces |
| **Converter tests** | All value converters with edge cases | xUnit, in-process |
| **Integration tests** | Full navigation flow, vault gate, data binding | Avalonia headless test framework |
| **Visual regression** | Screenshot comparison for key pages | Avalonia headless + image diff |

---

## 17  Future Considerations

| Item | Description | Priority |
|------|-------------|----------|
| **Dark/Light theme toggle** | User preference in Settings; Avalonia FluentTheme supports both | P1 |
| **Notification center** | Slide-out panel collecting all toasts/alerts for review | P2 |
| **Keyboard shortcuts** | Ctrl+1–8 for navigation, Ctrl+R refresh, Esc cancel | P2 |
| **Responsive layout** | Collapse sidebar to icons at narrow widths | P2 |
| **Custom dashboard widgets** | User-configurable dashboard layout with draggable tiles | P3 |
| **Remote desktop integration** | Launch RDP/SSH terminal from machine detail page | P3 |
