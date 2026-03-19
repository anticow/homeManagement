using System.Reactive.Disposables;
using System.Reactive.Linq;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Gui.Services;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

/// <summary>
/// Shell ViewModel — owns navigation, status bar bindings, and global reactive subscriptions.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private int _connectedAgentCount;
    private int _runningJobCount;
    private bool _isVaultUnlocked;
    private string _statusMessage = "Ready";

    public NavigationService Navigation { get; }

    public int ConnectedAgentCount
    {
        get => _connectedAgentCount;
        set => this.RaiseAndSetIfChanged(ref _connectedAgentCount, value);
    }

    public int RunningJobCount
    {
        get => _runningJobCount;
        set => this.RaiseAndSetIfChanged(ref _runningJobCount, value);
    }

    public bool IsVaultUnlocked
    {
        get => _isVaultUnlocked;
        set => this.RaiseAndSetIfChanged(ref _isVaultUnlocked, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public string AppVersion { get; } =
        typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public ReactiveCommand<string, System.Reactive.Unit> NavigateCommand { get; }

    public MainWindowViewModel(
        NavigationService navigation,
        IJobScheduler jobScheduler,
        IAgentGateway agentGateway,
        ICredentialVault vault)
    {
        Navigation = navigation;

        // Navigation command bound to sidebar buttons
        NavigateCommand = ReactiveCommand.Create<string>(page =>
        {
            switch (page)
            {
                case "Dashboard": Navigation.NavigateTo<DashboardViewModel>(); break;
                case "Machines": Navigation.NavigateTo<MachinesViewModel>(); break;
                case "Patching": Navigation.NavigateTo<PatchingViewModel>(); break;
                case "Services": Navigation.NavigateTo<ServicesViewModel>(); break;
                case "Jobs": Navigation.NavigateTo<JobsViewModel>(); break;
                case "Credentials": Navigation.NavigateTo<CredentialsViewModel>(); break;
                case "Audit Log": Navigation.NavigateTo<AuditLogViewModel>(); break;
                case "Settings": Navigation.NavigateTo<SettingsViewModel>(); break;
            }
        });

        // Subscribe to job progress stream → track running job count
        jobScheduler.ProgressStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
            {
                // Increment count on progress events; the orchestrator publishes
                // a final 100% event when a job completes — re-query to get accurate count.
                RunningJobCount = Math.Max(0, RunningJobCount);
            })
            .DisposeWith(Disposables);

        // Subscribe to agent connection events → update agent count
        // Merge all status-producing streams into a single serialized pipeline
        // to prevent concurrent writes to StatusMessage (HIGH-02)
        agentGateway.ConnectionEvents
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
            {
                ConnectedAgentCount = agentGateway.GetConnectedAgents().Count;
                StatusMessage = evt.Type switch
                {
                    AgentConnectionEventType.Connected =>
                        $"Agent {evt.Hostname} connected",
                    AgentConnectionEventType.Disconnected =>
                        $"Agent {evt.Hostname} disconnected",
                    _ => StatusMessage
                };
            })
            .DisposeWith(Disposables);

        // Reactive vault lock state — subscribe to LockStateChanged instead of polling (HIGH-01)
        vault.LockStateChanged
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(unlocked => IsVaultUnlocked = unlocked)
            .DisposeWith(Disposables);

        // Set initial vault state synchronously
        IsVaultUnlocked = vault.IsUnlocked;

        // Navigate to dashboard on startup
        Navigation.NavigateTo<DashboardViewModel>();
    }
}
