using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class PatchingViewModel : ViewModelBase
{
    private readonly IPatchService _patchService;
    private readonly IJobScheduler _jobScheduler;
    private readonly IInventoryService _inventory;
    private readonly IAgentGateway _agentGateway;

    private ObservableCollection<Machine> _machines = [];
    private ObservableCollection<PatchInfo> _detectedPatches = [];
    private Machine? _selectedMachine;
    private string? _statusMessage;

    public ObservableCollection<Machine> Machines
    {
        get => _machines;
        set => this.RaiseAndSetIfChanged(ref _machines, value);
    }

    public ObservableCollection<PatchInfo> DetectedPatches
    {
        get => _detectedPatches;
        set => this.RaiseAndSetIfChanged(ref _detectedPatches, value);
    }

    public Machine? SelectedMachine
    {
        get => _selectedMachine;
        set => this.RaiseAndSetIfChanged(ref _selectedMachine, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ScanCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ApplyCommand { get; }

    public PatchingViewModel(
        IPatchService patchService,
        IJobScheduler jobScheduler,
        IInventoryService inventory,
        IAgentGateway agentGateway)
    {
        _patchService = patchService;
        _jobScheduler = jobScheduler;
        _inventory = inventory;
        _agentGateway = agentGateway;

        ScanCommand = ReactiveCommand.CreateFromTask(ScanForPatchesAsync);
        ApplyCommand = ReactiveCommand.CreateFromTask(ApplyPatchesAsync);
        TrackErrors(ScanCommand);
        TrackErrors(ApplyCommand);

        // Load machines on construction
        LoadMachinesAsync().ConfigureAwait(false);

        // Auto-refresh machine list when agents connect or disconnect
        _agentGateway.ConnectionEvents
            .Throttle(TimeSpan.FromSeconds(3))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => LoadMachinesAsync().ConfigureAwait(false))
            .DisposeWith(Disposables);
    }

    private async Task ScanForPatchesAsync(CancellationToken ct)
    {
        await RunSafe(async () =>
        {
            if (_selectedMachine is null)
            {
                ErrorMessage = "Select a machine first.";
                return;
            }

            var target = ToTarget(_selectedMachine);
            var patches = await _patchService.DetectAsync(target, ct);
            DetectedPatches = new ObservableCollection<PatchInfo>(patches);
        });
    }

    private async Task LoadMachinesAsync(CancellationToken ct = default)
    {
        var machines = await _inventory.QueryAsync(new MachineQuery(), ct);
        Machines = new ObservableCollection<Machine>(machines.Items);
    }

    private async Task ApplyPatchesAsync(CancellationToken ct)
    {
        if (_selectedMachine is null || DetectedPatches.Count == 0)
        {
            ErrorMessage = "Select a machine and scan for patches first.";
            return;
        }

        var jobId = await _jobScheduler.SubmitAsync(new JobDefinition(
            Name: $"Patch Apply — {_selectedMachine.Hostname}",
            Type: Abstractions.JobType.PatchApply,
            TargetMachineIds: [_selectedMachine.Id],
            Parameters: []), ct);

        StatusMessage = $"Patch apply job submitted ({jobId}). Results will persist to the database.";
    }

    private static MachineTarget ToTarget(Machine m) =>
        new(m.Id, m.Hostname, m.OsType, m.ConnectionMode, m.Protocol, m.Port, m.CredentialId);
}
