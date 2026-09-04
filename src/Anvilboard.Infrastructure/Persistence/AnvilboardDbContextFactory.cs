using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Anvilboard.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so `dotnet ef migrations add` works from this class library without
/// needing to spin up the API host or its DI container.
/// </summary>
public sealed class AnvilboardDbContextFactory : IDesignTimeDbContextFactory<AnvilboardDbContext>
{
    public AnvilboardDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AnvilboardDbContext>();
        optionsBuilder.UseSqlite("Data Source=anvilboard.design.db");
        return new AnvilboardDbContext(optionsBuilder.Options);
    }
}
