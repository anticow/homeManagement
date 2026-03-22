using HomeManagement.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace HomeManagement.Integration.Tests;

/// <summary>
/// Shared SQL Server container fixture for integration tests.
/// A single container is reused across all tests in the collection.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>Creates a fresh DbContext with the container's connection string.</summary>
    public HomeManagementDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        var context = new HomeManagementDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}

[CollectionDefinition("SqlServer")]
public class SqlServerTestGroup : ICollectionFixture<SqlServerFixture>;
