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
    private readonly IEndpointStateProvider? _stateProvider;

    private Guid _machineId;
    private Machine? _machine;
    private ObservableCollection<InstalledPatch> _patches = [];
    private ObservableCollection<ServiceInfo> _services = [];
    private bool _endpointOnline;
    private double? _cpuPercent;
    private string _memoryDisplay = "—";
    private string _diskDisplay = "—";
    private string _uptimeDisplay = "—";
    private bool _hasLiveMetrics;

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

    public bool EndpointOnline
    {
        get => _endpointOnline;
        set => this.RaiseAndSetIfChanged(ref _endpointOnline, value);
    }

    public double? CpuPercent
    {
        get => _cpuPercent;
        set => this.RaiseAndSetIfChanged(ref _cpuPercent, value);
    }

    public string MemoryDisplay
    {
        get => _memoryDisplay;
        set => this.RaiseAndSetIfChanged(ref _memoryDisplay, value);
    }

    public string DiskDisplay
    {
        get => _diskDisplay;
        set => this.RaiseAndSetIfChanged(ref _diskDisplay, value);
    }

    public string UptimeDisplay
    {
        get => _uptimeDisplay;
        set => this.RaiseAndSetIfChanged(ref _uptimeDisplay, value);
    }

    public bool HasLiveMetrics
    {
        get => _hasLiveMetrics;
        set => this.RaiseAndSetIfChanged(ref _hasLiveMetrics, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }

    public MachineDetailViewModel(
        IInventoryService inventory,
        IPatchService patchService,
        IServiceController serviceController,
        IEndpointStateProvider? stateProvider = null)
    {
        _inventory = inventory;
        _patchService = patchService;
        _serviceController = serviceController;
        _stateProvider = stateProvider;

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

                var installedTask = _patchService.GetInstalledAsync(target, ct);
                var servicesTask = _serviceController.ListServicesAsync(target, ct: ct);

                // Fetch Prometheus metrics in parallel with installed patches / services
                Task<bool> onlineTask = Task.FromResult(false);
                Task<HardwareMetrics?> metricsTask = Task.FromResult<HardwareMetrics?>(null);

                if (_stateProvider is not null)
                {
                    onlineTask = _stateProvider.GetEndpointOnlineAsync(Machine.Hostname.Value, ct);
                    metricsTask = _stateProvider.GetHardwareMetricsAsync(Machine.Hostname.Value, Machine.OsType, ct);
                }

                await Task.WhenAll(installedTask, servicesTask, onlineTask, metricsTask);

                Patches = new ObservableCollection<InstalledPatch>(installedTask.Result);
                Services = new ObservableCollection<ServiceInfo>(servicesTask.Result);

                EndpointOnline = onlineTask.Result;
                var m = metricsTask.Result;
                HasLiveMetrics = m is not null;

                if (m is not null)
                {
                    CpuPercent = m.CpuUsagePercent;
                    MemoryDisplay = FormatMemory(m.MemoryUsedBytes, m.MemoryTotalBytes);
                    DiskDisplay = FormatDisk(m.DiskFreeBytes, m.DiskTotalBytes);
                    UptimeDisplay = FormatUptime(m.Uptime);
                }
            }
        });
    }

    private static string FormatMemory(long? used, long? total) =>
        (used, total) switch
        {
            (not null, not null) => $"{FormatGb(used.Value)} / {FormatGb(total.Value)} GB",
            (null, not null) => $"— / {FormatGb(total.Value)} GB",
            _ => "—"
        };

    private static string FormatDisk(long? free, long? total) =>
        (free, total) switch
        {
            (not null, not null) => $"{FormatGb(free.Value)} GB free of {FormatGb(total.Value)} GB",
            _ => "—"
        };

    private static string FormatGb(long bytes) =>
        $"{bytes / (1024.0 * 1024.0 * 1024.0):F1}";

    private static string FormatUptime(TimeSpan? ts) =>
        ts switch
        {
            null => "—",
            { TotalDays: >= 1 } t => $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m",
            { } t => $"{t.Hours}h {t.Minutes}m"
        };
}
