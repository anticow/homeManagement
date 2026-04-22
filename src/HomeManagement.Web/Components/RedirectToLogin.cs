using Microsoft.AspNetCore.Components;

namespace HomeManagement.Web.Components;

/// <summary>
/// Redirects unauthenticated users to the login page.
/// </summary>
public sealed class RedirectToLogin : ComponentBase
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    protected override void OnInitialized()
    {
        Navigation.NavigateTo("/login", forceLoad: true);
    }
}
