using HomeManagement.Abstractions.Repositories;
using HomeManagement.Data.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace HomeManagement.Data;

/// <summary>
/// Extension methods to register Auth repository implementations from the Data layer.
/// </summary>
public static class AuthDataServiceExtensions
{
    /// <summary>
    /// Register auth repository implementations. Call after registering the DbContext.
    /// </summary>
    public static IServiceCollection AddHomeManagementAuthRepositories(
        this IServiceCollection services)
    {
        services.AddScoped<IAuthUserRepository, AuthUserRepository>();
        services.AddScoped<IAuthRoleRepository, AuthRoleRepository>();
        services.AddScoped<IAuthRefreshTokenRepository, AuthRefreshTokenRepository>();
        services.AddScoped<IAuthDatabaseInitializer, AuthDatabaseInitializer>();
        return services;
    }
}
