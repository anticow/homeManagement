using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace HomeManagement.Auth;

/// <summary>
/// Extension methods to register the Auth subsystem in the DI container.
/// </summary>
public static class AuthServiceExtensions
{
    /// <summary>
    /// Register authentication services (JWT, local auth, RBAC).
    /// Active Directory provider is registered only if AD config is present.
    /// </summary>
    public static IServiceCollection AddHomeManagementAuth(
        this IServiceCollection services,
        Action<AuthOptions> configureOptions)
    {
        services.Configure(configureOptions);

        services.AddSingleton<JwtTokenService>();
        services.AddSingleton<LocalAuthProvider>();
        services.AddSingleton<RbacService>();
        services.AddScoped<AuthService>();
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, JwtBearerPostConfigureOptions>();

        // AD provider is registered only if configuration is present
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AuthOptions>>();
            if (options.Value.ActiveDirectory is null)
                throw new InvalidOperationException(
                    "Active Directory is not configured. Do not resolve ActiveDirectoryProvider when AD config is absent.");

            return new ActiveDirectoryProvider(options, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ActiveDirectoryProvider>>());
        });

        return services;
    }
}
