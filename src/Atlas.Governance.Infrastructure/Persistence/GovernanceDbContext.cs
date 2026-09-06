using Atlas.Application.Tenants;
using Atlas.Governance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Governance.Infrastructure.Persistence;

/// <summary>
/// The governance module's own persistence boundary: Postgres schema <c>governance</c>, its own
/// migrations history table, no foreign keys into the core schema (references by Guid only), and the same
/// tenant filter discipline as the core context.
/// </summary>
public sealed class GovernanceDbContext(DbContextOptions<GovernanceDbContext> options, ITenantContext? tenant = null) : DbContext(options)
{
    public const string Schema = "governance";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    private readonly ITenantContext _tenant = tenant ?? SystemTenantContext.Instance;

    public Guid? CurrentTenantId => _tenant.TenantId;

    public DbSet<CostSource> CostSources => Set<CostSource>();

    public DbSet<CostFact> CostFacts => Set<CostFact>();

    public DbSet<SeatFact> SeatFacts => Set<SeatFact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<CostSource>(entity =>
        {
            entity.ToTable("cost_sources");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.Provider).HasMaxLength(40).IsRequired();
            entity.Property(s => s.CredentialName).HasMaxLength(100).IsRequired();
            entity.Property(s => s.Scope).HasMaxLength(CostSource.MaxScopeLength);
            entity.Property(s => s.LastSyncStatus).HasMaxLength(20);
            entity.Property(s => s.LastSyncError).HasMaxLength(500);
            entity.HasIndex(s => new { s.TenantId, s.Provider }).IsUnique();
            entity.HasQueryFilter(s => _tenant.TenantId == null || s.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<CostFact>(entity =>
        {
            entity.ToTable("cost_facts");
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Id).ValueGeneratedNever();
            entity.Property(f => f.Provider).HasMaxLength(40).IsRequired();
            entity.Property(f => f.Basis).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(f => f.Dimension).HasMaxLength(40).IsRequired();
            entity.Property(f => f.DimensionKey).HasMaxLength(200).IsRequired();
            entity.Property(f => f.Detail).HasMaxLength(200);
            entity.Property(f => f.Amount).HasPrecision(18, 6);
            entity.Property(f => f.Currency).HasMaxLength(3).IsRequired();
            entity.Property(f => f.Quantity).HasPrecision(20, 4);
            entity.Property(f => f.Unit).HasMaxLength(30);
            entity.Property(f => f.PriceCatalogVersion).HasMaxLength(60).IsRequired();
            entity.HasIndex(f => new { f.TenantId, f.Period });
            entity.HasIndex(f => new { f.SourceId, f.Period });
            entity.HasQueryFilter(f => _tenant.TenantId == null || f.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<SeatFact>(entity =>
        {
            entity.ToTable("seat_facts");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.Provider).HasMaxLength(40).IsRequired();
            entity.Property(s => s.SeatKey).HasMaxLength(64).IsRequired();
            entity.Property(s => s.Plan).HasMaxLength(40);
            entity.HasIndex(s => new { s.SourceId, s.SeatKey }).IsUnique();
            entity.HasIndex(s => s.TenantId);
            entity.HasQueryFilter(s => _tenant.TenantId == null || s.TenantId == _tenant.TenantId);
        });
    }
}
