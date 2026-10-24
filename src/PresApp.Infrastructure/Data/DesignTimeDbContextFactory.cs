using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PresApp.Infrastructure.Data;

/// <summary>Used only by "dotnet ef" to build migrations; it never connects to a database.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PresAppDbContext>
{
    public PresAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PresAppDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=PresApp_Design;Trusted_Connection=True")
            .Options);
}
