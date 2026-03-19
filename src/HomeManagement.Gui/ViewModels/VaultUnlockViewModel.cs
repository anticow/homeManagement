using System.Security;
using HomeManagement.Abstractions.Interfaces;
using HomeManagement.Gui.Services;
using ReactiveUI;

namespace HomeManagement.Gui.ViewModels;

public sealed class VaultUnlockViewModel : ViewModelBase
{
    private readonly ICredentialVault _vault;
    private readonly NavigationService _navigation;

    public ReactiveCommand<SecureString, System.Reactive.Unit> UnlockCommand { get; }

    public VaultUnlockViewModel(ICredentialVault vault, NavigationService navigation)
    {
        _vault = vault;
        _navigation = navigation;

        UnlockCommand = ReactiveCommand.CreateFromTask<SecureString>(async (password, ct) =>
        {
            await _vault.UnlockAsync(password, ct);
            _navigation.GoBack();
        });
        TrackErrors(UnlockCommand);
    }
}
