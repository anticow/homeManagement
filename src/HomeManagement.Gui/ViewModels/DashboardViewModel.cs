using System.Reactive.Disposables;
using System.Reactive.Linq;
using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class DashboardViewModel : ViewModelBase
{
    private readonly ISystemHealthService _healthService;
    private readonly IJobScheduler _jobScheduler;
    private readonly IInventoryService _inventory;
    private readonly IAgentGateway _agentGateway;

    private SystemHealthReport? _healthReport;
    private IReadOnlyList<JobSummary> _recentJobs = [];
    private int _machineCount;
    private IReadOnlyList<ConnectedAgent> _connectedAgents = [];

    public SystemHealthReport? HealthReport
    {
        get => _healthReport;
        set => this.RaiseAndSetIfChanged(ref _healthReport, value);
    }

    public IReadOnlyList<JobSummary> RecentJobs
    {
        get => _recentJobs;
        set => this.RaiseAndSetIfChanged(ref _recentJobs, value);
    }

    public int MachineCount
    {
        get => _machineCount;
        set => this.RaiseAndSetIfChanged(ref _machineCount, value);
    }

    public IReadOnlyList<ConnectedAgent> ConnectedAgents
    {
        get => _connectedAgents;
        set => this.RaiseAndSetIfChanged(ref _connectedAgents, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }

    public DashboardViewModel(
        ISystemHealthService healthService,
        IJobScheduler jobScheduler,
        IInventoryService inventory,
        IAgentGateway agentGateway)
    {
        _healthService = healthService;
        _jobScheduler = jobScheduler;
        _inventory = inventory;
        _agentGateway = agentGateway;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadDataAsync);
        TrackErrors(RefreshCommand);

        // Auto-load on construction
        RefreshCommand.Execute().Subscribe().DisposeWith(Disposables);

        // Auto-refresh when agents connect or disconnect
        _agentGateway.ConnectionEvents
            .Throttle(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .SelectMany(_ => RefreshCommand.Execute())
            .Subscribe()
            .DisposeWith(Disposables);
    }

    private async Task LoadDataAsync(CancellationToken ct)
    {
        HealthReport = await _healthService.CheckAsync(ct);
        var jobs = await _jobScheduler.ListJobsAsync(new JobQuery(PageSize: 5), ct);
        RecentJobs = jobs.Items;
        var machines = await _inventory.QueryAsync(new MachineQuery(PageSize: 1), ct);
        MachineCount = machines.TotalCount;
        ConnectedAgents = _agentGateway.GetConnectedAgents();
    }
}
