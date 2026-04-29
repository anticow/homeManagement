using Microsoft.Extensions.Configuration;
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
    /// Registers the Auth subsystem using settings bound from <paramref name="configuration"/>,
    /// reading from the <see cref="AuthOptions.SectionName"/> section.
    /// </summary>
    public static IServiceCollection AddHomeManagementAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddHomeManagementAuth(options =>
            configuration.GetSection(AuthOptions.SectionName).Bind(options));
    }

    /// <summary>
    /// Register authentication services (JWT, local auth, RBAC, password policy).
    /// Active Directory provider is registered only if AD config is present.
    /// </summary>
    public static IServiceCollection AddHomeManagementAuth(
        this IServiceCollection services,
        Action<AuthOptions> configureOptions)
    {
        // Snapshot options at registration time to enable conditional DI wiring.
        var opts = new AuthOptions();
        configureOptions(opts);
        services.Configure(configureOptions);

        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IPasswordPolicy, DefaultPasswordPolicy>();
        services.AddSingleton<LocalAuthProvider>();
        services.AddSingleton<RbacService>();
        services.AddScoped<AuthService>();
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, JwtBearerPostConfigureOptions>();

        if (opts.ActiveDirectory is not null)
            services.AddSingleton<ActiveDirectoryProvider>();

        return services;
    }
}
