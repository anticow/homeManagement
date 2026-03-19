using HomeManagement.Abstractions.CrossCutting;
using HomeManagement.Abstractions.Interfaces;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ICredentialVault _vault;
    private readonly ISystemHealthService _health;

    private bool _vaultUnlocked;
    private SystemHealthReport? _healthReport;

    public bool VaultUnlocked
    {
        get => _vaultUnlocked;
        set => this.RaiseAndSetIfChanged(ref _vaultUnlocked, value);
    }

    public SystemHealthReport? HealthReport
    {
        get => _healthReport;
        set => this.RaiseAndSetIfChanged(ref _healthReport, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SaveCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshHealthCommand { get; }

    public SettingsViewModel(ICredentialVault vault, ISystemHealthService health)
    {
        _vault = vault;
        _health = health;

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        RefreshHealthCommand = ReactiveCommand.CreateFromTask(RefreshHealthAsync);
        TrackErrors(SaveCommand);
        TrackErrors(RefreshHealthCommand);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await RunSafe(async () =>
        {
            // Persist any changed settings
            VaultUnlocked = _vault.IsUnlocked;
            await Task.CompletedTask;
        });
    }

    private async Task RefreshHealthAsync(CancellationToken ct)
    {
        await RunSafe(async () =>
        {
            HealthReport = await _health.CheckAsync(ct);
            VaultUnlocked = _vault.IsUnlocked;
        });
    }
}
