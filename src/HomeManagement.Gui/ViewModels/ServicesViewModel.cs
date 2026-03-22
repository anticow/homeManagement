using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Abstractions.Validation;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class ServicesViewModel : ViewModelBase
{
    private readonly IServiceController _serviceController;
    private readonly IInventoryService _inventory;
    private readonly IAgentGateway _agentGateway;

    private ObservableCollection<ServiceInfo> _services = [];
    private ObservableCollection<Machine> _machines = [];
    private Machine? _selectedMachine;
    private string? _statusMessage;

    public ObservableCollection<ServiceInfo> Services
    {
        get => _services;
        set => this.RaiseAndSetIfChanged(ref _services, value);
    }

    public ObservableCollection<Machine> Machines
    {
        get => _machines;
        set => this.RaiseAndSetIfChanged(ref _machines, value);
    }

    public Machine? SelectedMachine
    {
        get => _selectedMachine;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMachine, value);
            if (value is not null)
                RefreshCommand.Execute().Subscribe();
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveCommand<ServiceInfo, System.Reactive.Unit> StartServiceCommand { get; }
    public ReactiveCommand<ServiceInfo, System.Reactive.Unit> StopServiceCommand { get; }
    public ReactiveCommand<ServiceInfo, System.Reactive.Unit> RestartServiceCommand { get; }

    public ServicesViewModel(
        IServiceController serviceController,
        IInventoryService inventory,
        IAgentGateway agentGateway)
    {
        _serviceController = serviceController;
        _inventory = inventory;
        _agentGateway = agentGateway;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadServicesAsync);
        StartServiceCommand = ReactiveCommand.CreateFromTask<ServiceInfo>(svc => ControlServiceAsync(svc, ServiceAction.Start));
        StopServiceCommand = ReactiveCommand.CreateFromTask<ServiceInfo>(svc => ControlServiceAsync(svc, ServiceAction.Stop));
        RestartServiceCommand = ReactiveCommand.CreateFromTask<ServiceInfo>(svc => ControlServiceAsync(svc, ServiceAction.Restart));
        TrackErrors(RefreshCommand);
        TrackErrors(StartServiceCommand);
        TrackErrors(StopServiceCommand);
        TrackErrors(RestartServiceCommand);

        // Load machine list on construction
        LoadMachinesAsync().ConfigureAwait(false);

        // Auto-refresh machine list when agents connect or disconnect
        _agentGateway.ConnectionEvents
            .Throttle(TimeSpan.FromSeconds(3))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => LoadMachinesAsync().ConfigureAwait(false))
            .DisposeWith(Disposables);
    }

    private async Task LoadServicesAsync(CancellationToken ct)
    {
        await RunSafe(async () =>
        {
            if (_selectedMachine is null) return;

            var target = ToTarget(_selectedMachine);
            var list = await _serviceController.ListServicesAsync(target, ct: ct);
            Services = new ObservableCollection<ServiceInfo>(list);
        });
    }

    private async Task ControlServiceAsync(ServiceInfo svc, ServiceAction action)
    {
        await RunSafe(async () =>
        {
            if (_selectedMachine is null) return;

            var target = ToTarget(_selectedMachine);

            var result = await _serviceController.ControlAsync(target, svc.Name, action);
            StatusMessage = result.Success
                ? $"{action} {svc.Name} completed on {_selectedMachine.Hostname}."
                : $"{action} {svc.Name} failed on {_selectedMachine.Hostname}: {result.ErrorMessage}";
            await LoadServicesAsync(CancellationToken.None);
        });
    }

    private static MachineTarget ToTarget(Machine m) =>
        new(m.Id, m.Hostname, m.OsType, m.ConnectionMode, m.Protocol, m.Port, m.CredentialId);

    private async Task LoadMachinesAsync(CancellationToken ct = default)
    {
        var result = await _inventory.QueryAsync(new MachineQuery(), ct);
        Machines = new ObservableCollection<Machine>(result.Items);
    }
}
