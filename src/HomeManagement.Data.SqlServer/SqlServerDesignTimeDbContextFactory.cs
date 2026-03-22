using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using HomeManagement.Data;

namespace HomeManagement.Data.SqlServer;

/// <summary>
/// Design-time factory for generating SQL Server migrations.
/// Usage: dotnet ef migrations add &lt;Name&gt; --project src/HomeManagement.Data.SqlServer --startup-project src/HomeManagement.Data.SqlServer
/// </summary>
public sealed class SqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<HomeManagementDbContext>
{
    public HomeManagementDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlServer("Server=localhost;Database=HomeManagement;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;

        return new HomeManagementDbContext(options);
    }
}
