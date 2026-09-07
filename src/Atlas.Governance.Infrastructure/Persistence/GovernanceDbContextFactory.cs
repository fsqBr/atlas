using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Atlas.Governance.Infrastructure.Persistence;

/// <summary>Design-time only (dotnet ef): the connection string is a placeholder, migrations never run from here.</summary>
public sealed class GovernanceDbContextFactory : IDesignTimeDbContextFactory<GovernanceDbContext>
{
    public GovernanceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GovernanceDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=atlas;Username=atlas;Password=design-time-only",
                npgsql => npgsql.MigrationsHistoryTable(GovernanceDbContext.MigrationsHistoryTable, GovernanceDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new GovernanceDbContext(options);
    }
}
