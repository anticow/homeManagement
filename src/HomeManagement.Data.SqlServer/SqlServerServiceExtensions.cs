using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using HomeManagement.Data;

namespace HomeManagement.Data.SqlServer;

/// <summary>
/// Extension methods to register the SQL Server EF Core provider.
/// </summary>
public static class SqlServerServiceExtensions
{
    /// <summary>
    /// Register <see cref="HomeManagementDbContext"/> with SQL Server.
    /// In .NET 10, EF Core strictly enforces one provider per service provider.
    /// Skip registration if a DbContext provider is already configured.
    /// </summary>
    public static IServiceCollection AddHomeManagementSqlServer(
        this IServiceCollection services,
        string connectionString)
    {
        // Skip if another provider (e.g., SQLite in tests) is already registered
        if (services.Any(s => s.ServiceType == typeof(DbContextOptions<HomeManagementDbContext>)))
        {
            return services;
        }

        services.AddDbContext<HomeManagementDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(SqlServerServiceExtensions).Assembly.FullName);
                sql.EnableRetryOnFailure(maxRetryCount: 3);
                sql.CommandTimeout(30);
            }));

        return services;
    }
}
