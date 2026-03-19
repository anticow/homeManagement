using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class MachineDetailViewModel : ViewModelBase
{
    private readonly IInventoryService _inventory;
    private readonly IPatchService _patchService;
    private readonly IServiceController _serviceController;

    private Guid _machineId;
    private Machine? _machine;
    private ObservableCollection<InstalledPatch> _patches = [];
    private ObservableCollection<ServiceInfo> _services = [];

    public Guid MachineId
    {
        get => _machineId;
        set => this.RaiseAndSetIfChanged(ref _machineId, value);
    }

    public Machine? Machine
    {
        get => _machine;
        set => this.RaiseAndSetIfChanged(ref _machine, value);
    }

    public ObservableCollection<InstalledPatch> Patches
    {
        get => _patches;
        set => this.RaiseAndSetIfChanged(ref _patches, value);
    }

    public ObservableCollection<ServiceInfo> Services
    {
        get => _services;
        set => this.RaiseAndSetIfChanged(ref _services, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }

    public MachineDetailViewModel(
        IInventoryService inventory,
        IPatchService patchService,
        IServiceController serviceController)
    {
        _inventory = inventory;
        _patchService = patchService;
        _serviceController = serviceController;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadDataAsync);
        TrackErrors(RefreshCommand);
    }

    private async Task LoadDataAsync(CancellationToken ct)
    {
        await RunSafe(async () =>
        {
            if (_machineId == Guid.Empty) return;

            Machine = await _inventory.GetAsync(_machineId, ct);

            if (Machine is not null)
            {
                var target = new MachineTarget(
                    Machine.Id, Machine.Hostname, Machine.OsType,
                    Machine.ConnectionMode, Machine.Protocol,
                    Machine.Port, Machine.CredentialId);

                var installed = await _patchService.GetInstalledAsync(target, ct);
                Patches = new ObservableCollection<InstalledPatch>(installed);

                var svcList = await _serviceController.ListServicesAsync(target, ct: ct);
                Services = new ObservableCollection<ServiceInfo>(svcList);
            }
        });
    }
}
