using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using HomeManagement.Abstractions;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class AuditLogViewModel : ViewModelBase
{
    private readonly IAuditLogger _auditLogger;

    private ObservableCollection<AuditEvent> _events = [];
    private int _totalCount;
    private AuditAction? _actionFilter;
    private DateTime? _fromUtc;
    private DateTime? _toUtc;
    private int _page = 1;

    public ObservableCollection<AuditEvent> Events
    {
        get => _events;
        set => this.RaiseAndSetIfChanged(ref _events, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        set => this.RaiseAndSetIfChanged(ref _totalCount, value);
    }

    public AuditAction? ActionFilter
    {
        get => _actionFilter;
        set => this.RaiseAndSetIfChanged(ref _actionFilter, value);
    }

    public DateTime? FromUtc
    {
        get => _fromUtc;
        set => this.RaiseAndSetIfChanged(ref _fromUtc, value);
    }

    public DateTime? ToUtc
    {
        get => _toUtc;
        set => this.RaiseAndSetIfChanged(ref _toUtc, value);
    }

    public int Page
    {
        get => _page;
        set => this.RaiseAndSetIfChanged(ref _page, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> NextPageCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> PrevPageCommand { get; }

    public AuditLogViewModel(IAuditLogger auditLogger)
    {
        _auditLogger = auditLogger;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadEventsAsync);
        NextPageCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            Page++;
            await LoadEventsAsync(ct);
        });
        PrevPageCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            if (Page > 1) Page--;
            await LoadEventsAsync(ct);
        });
        TrackErrors(RefreshCommand);
        TrackErrors(NextPageCommand);
        TrackErrors(PrevPageCommand);

        RefreshCommand.Execute().Subscribe().DisposeWith(Disposables);
    }

    private async Task LoadEventsAsync(CancellationToken ct)
    {
        var result = await _auditLogger.QueryAsync(new AuditQuery(
            Action: ActionFilter,
            FromUtc: FromUtc,
            ToUtc: ToUtc,
            Page: Page), ct);
        Events = new ObservableCollection<AuditEvent>(result.Items);
        TotalCount = result.TotalCount;
    }
}
