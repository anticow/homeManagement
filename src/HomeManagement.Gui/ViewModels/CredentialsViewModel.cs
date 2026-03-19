using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Security;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Abstractions.Models;
using HomeManagement.Gui.Services;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class CredentialsViewModel : ViewModelBase
{
    private readonly ICredentialVault _vault;
    private readonly NavigationService _navigation;

    private ObservableCollection<CredentialEntry> _credentials = [];

    public ObservableCollection<CredentialEntry> Credentials
    {
        get => _credentials;
        set => this.RaiseAndSetIfChanged(ref _credentials, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> RefreshCommand { get; }
    public ReactiveCommand<Guid, System.Reactive.Unit> RemoveCommand { get; }

    public CredentialsViewModel(ICredentialVault vault, NavigationService navigation)
    {
        _vault = vault;
        _navigation = navigation;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadCredentialsAsync);
        TrackErrors(RefreshCommand);

        RemoveCommand = ReactiveCommand.CreateFromTask<Guid>(async (id, ct) =>
        {
            await _vault.RemoveAsync(id, ct);
            await LoadCredentialsAsync(ct);
        });
        TrackErrors(RemoveCommand);

        RefreshCommand.Execute().Subscribe().DisposeWith(Disposables);
    }

    private async Task LoadCredentialsAsync(CancellationToken ct)
    {
        if (!_vault.IsUnlocked)
        {
            _navigation.NavigateTo<VaultUnlockViewModel>();
            return;
        }

        var list = await _vault.ListAsync(ct);
        Credentials = new ObservableCollection<CredentialEntry>(list);
    }
}
