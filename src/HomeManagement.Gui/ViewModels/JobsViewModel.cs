using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class JobsViewModel : ViewModelBase
{
    private readonly IJobScheduler _jobScheduler;

    private ObservableCollection<JobSummary> _jobs = [];
    private int _totalCount;

    public ObservableCollection<JobSummary> Jobs
    {
        get => _jobs;
        set => this.RaiseAndSetIfChanged(ref _jobs, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        set => this.RaiseAndSetIfChanged(ref _totalCount, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveCommand<JobId, System.Reactive.Unit> CancelJobCommand { get; }

    public JobsViewModel(IJobScheduler jobScheduler, ICommandBroker broker)
    {
        _jobScheduler = jobScheduler;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadJobsAsync);
        TrackErrors(RefreshCommand);

        CancelJobCommand = ReactiveCommand.CreateFromTask<JobId>(async (jobId, ct) =>
        {
            await _jobScheduler.CancelAsync(jobId, ct);
            await LoadJobsAsync(ct);
        });
        TrackErrors(CancelJobCommand);

        // Live progress updates from the scheduler's progress stream
        _jobScheduler.ProgressStream
            .ObserveOn(RxApp.MainThreadScheduler)
            .SelectMany(_ => RefreshCommand.Execute())
            .Subscribe()
            .DisposeWith(Disposables);

        // Also refresh when the broker completes any command (job results written to DB)
        broker.CompletedStream
            .Where(evt => evt.JobId.HasValue)
            .Throttle(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .SelectMany(_ => RefreshCommand.Execute())
            .Subscribe()
            .DisposeWith(Disposables);

        RefreshCommand.Execute().Subscribe().DisposeWith(Disposables);
    }

    private async Task LoadJobsAsync(CancellationToken ct)
    {
        var result = await _jobScheduler.ListJobsAsync(new JobQuery(), ct);
        Jobs = new ObservableCollection<JobSummary>(result.Items);
        TotalCount = result.TotalCount;
    }
}
