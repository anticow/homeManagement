using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HomeManagement.Data;

/// <summary>
/// Design-time factory used by EF Core CLI tools (dotnet ef migrations add, etc.).
/// Points to a temporary SQLite database so the tools can scaffold migrations.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<HomeManagementDbContext>
{
    public HomeManagementDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<HomeManagementDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;

        return new HomeManagementDbContext(options);
    }
}
