using System.Collections.ObjectModel;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class JobDetailViewModel : ViewModelBase
{
    private readonly IJobScheduler _jobScheduler;

    private JobId? _jobId;
    private JobStatus? _status;
    private ObservableCollection<JobMachineResult> _machineResults = [];

    public JobId? JobId
    {
        get => _jobId;
        set => this.RaiseAndSetIfChanged(ref _jobId, value);
    }

    public JobStatus? Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public ObservableCollection<JobMachineResult> MachineResults
    {
        get => _machineResults;
        set => this.RaiseAndSetIfChanged(ref _machineResults, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CancelCommand { get; }

    public JobDetailViewModel(IJobScheduler jobScheduler)
    {
        _jobScheduler = jobScheduler;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadStatusAsync);
        CancelCommand = ReactiveCommand.CreateFromTask(CancelJobAsync);
        TrackErrors(RefreshCommand);
        TrackErrors(CancelCommand);
    }

    private async Task LoadStatusAsync(CancellationToken ct)
    {
        await RunSafe(async () =>
        {
            if (_jobId is null) return;

            Status = await _jobScheduler.GetStatusAsync(_jobId, ct);
            MachineResults = new ObservableCollection<JobMachineResult>(Status.MachineResults);
        });
    }

    private async Task CancelJobAsync(CancellationToken ct)
    {
        await RunSafe(async () =>
        {
            if (_jobId is null) return;

            await _jobScheduler.CancelAsync(_jobId, ct);
            await LoadStatusAsync(ct);
        });
    }
}
