using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Atlas.Infrastructure.Persistence;

/// <summary>Design-time factory so `dotnet ef migrations` works without a running host.</summary>
public sealed class AtlasDbContextFactory : IDesignTimeDbContextFactory<AtlasDbContext>
{
    public AtlasDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=atlas;Username=atlas;Password=design-time-only",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "atlas"))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AtlasDbContext(options);
    }
}
