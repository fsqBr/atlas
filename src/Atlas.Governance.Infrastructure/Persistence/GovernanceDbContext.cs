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

    public DbSet<UsageFact> UsageFacts => Set<UsageFact>();

    public DbSet<ModelPriceOverride> ModelPrices => Set<ModelPriceOverride>();

    public DbSet<UsageReportEvent> UsageReportEvents => Set<UsageReportEvent>();

    public DbSet<TelemetryStream> TelemetryStreams => Set<TelemetryStream>();

    public DbSet<Budget> Budgets => Set<Budget>();

    public DbSet<BudgetAlert> BudgetAlerts => Set<BudgetAlert>();

    public DbSet<Team> Teams => Set<Team>();

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

        modelBuilder.Entity<UsageFact>(entity =>
        {
            entity.ToTable("usage_facts");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).ValueGeneratedNever();
            entity.Property(u => u.Actor).HasMaxLength(UsageFact.MaxActorLength).IsRequired();
            entity.Property(u => u.Tool).HasMaxLength(UsageFact.MaxToolLength).IsRequired();
            entity.Property(u => u.Model).HasMaxLength(UsageFact.MaxModelLength).IsRequired();
            entity.Property(u => u.EstimatedCost).HasPrecision(18, 6);
            entity.Property(u => u.Currency).HasMaxLength(3).IsRequired();
            entity.Property(u => u.PriceCatalogVersion).HasMaxLength(60).IsRequired();
            entity.Property(u => u.Source).HasMaxLength(40).IsRequired();
            entity.Property(u => u.Provider).HasMaxLength(40);
            entity.Property(u => u.ReportedCost).HasPrecision(18, 6);
            entity.Ignore(u => u.TotalTokens);
            entity.HasIndex(u => new { u.TenantId, u.Actor, u.Tool, u.Period, u.Model }).IsUnique();
            entity.HasIndex(u => new { u.TenantId, u.Period });
            entity.HasQueryFilter(u => _tenant.TenantId == null || u.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<TelemetryStream>(entity =>
        {
            entity.ToTable("telemetry_streams");
            entity.HasKey(s => new { s.TenantId, s.StreamKey });
            entity.Property(s => s.StreamKey).HasMaxLength(64);
            entity.HasIndex(s => s.UpdatedAtUtc);
            entity.HasQueryFilter(s => _tenant.TenantId == null || s.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<Budget>(entity =>
        {
            entity.ToTable("budgets");
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Id).ValueGeneratedNever();
            entity.Property(b => b.Scope).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(b => b.ScopeKey).HasMaxLength(200);
            entity.Property(b => b.MonthlyAmount).HasPrecision(18, 2);
            entity.Property(b => b.Name).HasMaxLength(120);
            entity.Property(b => b.UpdatedBy).HasMaxLength(100).IsRequired();
            entity.Ignore(b => b.Label);
            entity.HasIndex(b => b.TenantId);
            entity.HasQueryFilter(b => _tenant.TenantId == null || b.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<BudgetAlert>(entity =>
        {
            entity.ToTable("budget_alerts");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedNever();
            entity.Property(a => a.Kind).HasMaxLength(20).IsRequired();
            entity.Property(a => a.Key).HasMaxLength(120).IsRequired();
            entity.Property(a => a.Message).HasMaxLength(500).IsRequired();
            entity.Property(a => a.Amount).HasPrecision(18, 2);
            entity.Property(a => a.Percent).HasPrecision(9, 1);
            entity.Property(a => a.DeliveryError).HasMaxLength(300);
            entity.HasIndex(a => new { a.TenantId, a.Key }).IsUnique();
            entity.HasIndex(a => new { a.TenantId, a.CreatedAtUtc });
            entity.HasQueryFilter(a => _tenant.TenantId == null || a.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.ToTable("teams");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).ValueGeneratedNever();
            entity.Property(t => t.Name).HasMaxLength(80).IsRequired();
            entity.Property(t => t.MembersJson).HasColumnType("jsonb").IsRequired();
            entity.Property(t => t.UpdatedBy).HasMaxLength(100).IsRequired();
            entity.Property(t => t.WebhookUrl).HasMaxLength(1000);
            entity.Property(t => t.SlackWebhookUrl).HasMaxLength(1000);
            entity.Property(t => t.TeamsWebhookUrl).HasMaxLength(1000);
            entity.Ignore(t => t.Members);
            entity.Ignore(t => t.HasChannels);
            entity.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
            entity.HasQueryFilter(t => _tenant.TenantId == null || t.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<UsageReportEvent>(entity =>
        {
            entity.ToTable("usage_report_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Actor).HasMaxLength(UsageFact.MaxActorLength).IsRequired();
            entity.Property(e => e.Tool).HasMaxLength(UsageFact.MaxToolLength).IsRequired();
            entity.Property(e => e.EstimatedCostToday).HasPrecision(18, 6);
            entity.HasIndex(e => new { e.TenantId, e.ReportedAtUtc });
            entity.HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<ModelPriceOverride>(entity =>
        {
            entity.ToTable("model_prices");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.Property(p => p.Pattern).HasMaxLength(ModelPriceOverride.MaxPatternLength).IsRequired();
            entity.Property(p => p.Input).HasPrecision(12, 4);
            entity.Property(p => p.Output).HasPrecision(12, 4);
            entity.Property(p => p.CacheRead).HasPrecision(12, 4);
            entity.Property(p => p.CacheWrite).HasPrecision(12, 4);
            entity.Property(p => p.Note).HasMaxLength(200);
            entity.Property(p => p.UpdatedBy).HasMaxLength(100).IsRequired();
            entity.HasIndex(p => new { p.TenantId, p.Pattern }).IsUnique();
            entity.HasQueryFilter(p => _tenant.TenantId == null || p.TenantId == _tenant.TenantId);
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
