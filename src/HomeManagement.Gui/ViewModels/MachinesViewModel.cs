using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Gui.Services;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class MachinesViewModel : ViewModelBase
{
    private readonly IInventoryService _inventory;
    private readonly IRemoteExecutor _executor;
    private readonly IAgentGateway _agentGateway;
    private readonly NavigationService _navigation;

    private string _searchText = string.Empty;
    private OsType? _osFilter;
    private MachineState? _stateFilter;
    private ObservableCollection<Machine> _machines = [];
    private int _totalCount;

    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public OsType? OsFilter
    {
        get => _osFilter;
        set => this.RaiseAndSetIfChanged(ref _osFilter, value);
    }

    public MachineState? StateFilter
    {
        get => _stateFilter;
        set => this.RaiseAndSetIfChanged(ref _stateFilter, value);
    }

    public ObservableCollection<Machine> Machines
    {
        get => _machines;
        set => this.RaiseAndSetIfChanged(ref _machines, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        set => this.RaiseAndSetIfChanged(ref _totalCount, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveCommand<Machine, System.Reactive.Unit> ViewDetailCommand { get; }
    public ReactiveCommand<Machine, System.Reactive.Unit> TestConnectionCommand { get; }

    public MachinesViewModel(
        IInventoryService inventory,
        IRemoteExecutor executor,
        IAgentGateway agentGateway,
        NavigationService navigation)
    {
        _inventory = inventory;
        _executor = executor;
        _agentGateway = agentGateway;
        _navigation = navigation;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadMachinesAsync);
        TrackErrors(RefreshCommand);

        ViewDetailCommand = ReactiveCommand.Create<Machine>(machine =>
        {
            // MachineDetailViewModel would be parameterized with machine ID
            _navigation.NavigateTo<MachineDetailViewModel>();
        });

        TestConnectionCommand = ReactiveCommand.CreateFromTask<Machine>(async (machine, ct) =>
        {
            var target = new MachineTarget(machine.Id, machine.Hostname, machine.OsType,
                machine.ConnectionMode, machine.Protocol, machine.Port, machine.CredentialId);
            await _executor.TestConnectionAsync(target, ct);
        });
        TrackErrors(TestConnectionCommand);

        RefreshCommand.Execute().Subscribe().DisposeWith(Disposables);

        // Auto-refresh when agents connect or disconnect (triggers machine registration)
        _agentGateway.ConnectionEvents
            .Throttle(TimeSpan.FromSeconds(3))
            .ObserveOn(RxApp.MainThreadScheduler)
            .SelectMany(_ => RefreshCommand.Execute())
            .Subscribe()
            .DisposeWith(Disposables);
    }

    private async Task LoadMachinesAsync(CancellationToken ct)
    {
        var result = await _inventory.QueryAsync(new MachineQuery(
            SearchText: SearchText,
            OsType: OsFilter,
            State: StateFilter), ct);
        Machines = new ObservableCollection<Machine>(result.Items);
        TotalCount = result.TotalCount;
    }
}
