using Atlas.Domain.Assessments;
using Atlas.Domain.Audit;
using Atlas.Domain.Credentials;
using Atlas.Domain.Findings;
using Atlas.Domain.Health;
using Atlas.Domain.Jobs;
using Atlas.Domain.Rules;
using Atlas.Domain.Scans;
using Atlas.Domain.Tenants;
using Atlas.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure.Persistence;

public sealed class AtlasDbContext(DbContextOptions<AtlasDbContext> options, Atlas.Application.Tenants.ITenantContext? tenant = null) : DbContext(options)
{
    private readonly Atlas.Application.Tenants.ITenantContext _tenant = tenant ?? Atlas.Application.Tenants.SystemTenantContext.Instance;

    /// <summary>Tenant applied by the global query filters: null in system scope (worker, migrations, background services).</summary>
    public Guid? CurrentTenantId => _tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<Assessment> Assessments => Set<Assessment>();

    public DbSet<AssessmentRun> AssessmentRuns => Set<AssessmentRun>();

    public DbSet<Scan> Scans => Set<Scan>();

    public DbSet<ScanJob> ScanJobs => Set<ScanJob>();

    public DbSet<RuleDefinition> RuleDefinitions => Set<RuleDefinition>();

    public DbSet<Finding> Findings => Set<Finding>();

    public DbSet<FindingOccurrence> FindingOccurrences => Set<FindingOccurrence>();

    public DbSet<FindingSuppression> FindingSuppressions => Set<FindingSuppression>();

    public DbSet<InventorySnapshot> InventorySnapshots => Set<InventorySnapshot>();

    public DbSet<HealthSnapshot> HealthSnapshots => Set<HealthSnapshot>();

    public DbSet<ConnectorCredential> ConnectorCredentials => Set<ConnectorCredential>();

    public DbSet<SuppressionPolicy> SuppressionPolicies => Set<SuppressionPolicy>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<Atlas.Domain.Ai.AiProviderSettings> AiProviderSettings => Set<Atlas.Domain.Ai.AiProviderSettings>();

    public DbSet<Atlas.Domain.Ai.BusinessRule> BusinessRules => Set<Atlas.Domain.Ai.BusinessRule>();

    public DbSet<Atlas.Domain.Ai.BusinessRuleAnalysis> BusinessRuleAnalyses => Set<Atlas.Domain.Ai.BusinessRuleAnalysis>();

    public DbSet<Atlas.Domain.Ai.AiNarrative> AiNarratives => Set<Atlas.Domain.Ai.AiNarrative>();

    public DbSet<Atlas.Domain.Security.ApiToken> ApiTokens => Set<Atlas.Domain.Security.ApiToken>();

    public DbSet<AssessmentAccess> AssessmentAccesses => Set<AssessmentAccess>();

    public DbSet<Atlas.Domain.Modernization.ModernizationActual> ModernizationActuals => Set<Atlas.Domain.Modernization.ModernizationActual>();

    public DbSet<Atlas.Domain.Rules.RuleSeverityOverride> RuleSeverityOverrides => Set<Atlas.Domain.Rules.RuleSeverityOverride>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("atlas");
        ApplyTenantFilters(modelBuilder);

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).ValueGeneratedNever();
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            entity.Property(t => t.ExternalKey).HasMaxLength(200);
            entity.HasIndex(t => t.ExternalKey).IsUnique();
            entity.Property(t => t.CreatedAtUtc).IsRequired();

            entity.HasData(new
            {
                Id = WellKnownTenants.DefaultId,
                Name = "Default",
                CreatedAtUtc = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero),
            });
        });

        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.ToTable("workspaces");
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Id).ValueGeneratedNever();
            entity.Property(w => w.TenantId).IsRequired();
            entity.Property(w => w.SourceKind).HasMaxLength(50).IsRequired();
            entity.Property(w => w.SourceLocator).HasMaxLength(2000).IsRequired();
            entity.Property(w => w.Branch).HasMaxLength(500);
            entity.Property(w => w.CommitSha).HasMaxLength(64);
            entity.Property(w => w.RootPath).HasMaxLength(1000).IsRequired();
            entity.Property(w => w.FailureReason).HasMaxLength(2000);
            entity.Property(w => w.State).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Ignore(w => w.History);

            entity.HasOne<Tenant>().WithMany().HasForeignKey(w => w.TenantId);
            entity.HasIndex(w => new { w.State, w.LeaseExpiresAtUtc });
        });

        modelBuilder.Entity<Assessment>(entity =>
        {
            entity.ToTable("assessments");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedNever();
            entity.Property(a => a.Name).HasMaxLength(200).IsRequired();
            entity.Property(a => a.SourceKind).HasMaxLength(50).IsRequired();
            entity.Property(a => a.SourceLocator).HasMaxLength(2000).IsRequired();
            entity.Property(a => a.Branch).HasMaxLength(500);
            entity.Property(a => a.CredentialName).HasMaxLength(ConnectorCredential.MaxNameLength);
            entity.Property(a => a.ExcludeGlobsJson).HasColumnType("jsonb");
            entity.Property(a => a.WebhookUrl).HasMaxLength(2000);
            entity.Ignore(a => a.ExcludeGlobs);
            entity.Property(a => a.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(a => a.FailureReason).HasMaxLength(2000);
            entity.Ignore(a => a.Source);
            entity.Ignore(a => a.RepositoryKey);

            entity.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId);
            entity.HasIndex(a => new { a.TenantId, a.CreatedAtUtc });
        });

        modelBuilder.Entity<AssessmentRun>(entity =>
        {
            entity.ToTable("assessment_runs");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.CommitSha).HasMaxLength(64);
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(r => r.FailureReason).HasMaxLength(2000);

            entity.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(r => r.AssessmentId);
            entity.HasIndex(r => new { r.AssessmentId, r.Number }).IsUnique();
        });

        modelBuilder.Entity<Scan>(entity =>
        {
            entity.ToTable("scans");
            entity.HasOne<AssessmentRun>().WithMany().HasForeignKey(s => s.RunId);
            entity.HasIndex(s => s.RunId);
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.ScannerId).HasMaxLength(100).IsRequired();
            entity.Property(s => s.ScannerVersion).HasMaxLength(50).IsRequired();
            entity.Property(s => s.CommitSha).HasMaxLength(64);
            entity.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(s => s.Error).HasMaxLength(4000);

            entity.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(s => s.AssessmentId);
            entity.HasIndex(s => new { s.AssessmentId, s.ScannerId, s.CommitSha, s.Status });
        });

        modelBuilder.Entity<ScanJob>(entity =>
        {
            entity.ToTable("scan_jobs");
            entity.HasKey(j => j.Id);
            entity.Property(j => j.Id).ValueGeneratedNever();
            entity.Property(j => j.State).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(j => j.Kind).HasMaxLength(40).IsRequired().HasDefaultValue(ScanJob.Kinds.Scan);
            entity.Property(j => j.Payload).HasMaxLength(ScanJob.MaxPayloadLength);
            entity.Property(j => j.LeasedBy).HasMaxLength(200);
            entity.Property(j => j.Error).HasMaxLength(4000);

            entity.HasOne<Tenant>().WithMany().HasForeignKey(j => j.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(j => j.AssessmentId);
            entity.HasIndex(j => new { j.State, j.QueuedAtUtc });
        });

        modelBuilder.Entity<RuleDefinition>(entity =>
        {
            entity.ToTable("rule_definitions");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasMaxLength(200).ValueGeneratedNever();
            entity.Property(r => r.ScannerId).HasMaxLength(100).IsRequired();
            entity.Property(r => r.Version).HasMaxLength(50).IsRequired();
            entity.Property(r => r.Category).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(r => r.DefaultSeverity).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(r => r.Title).HasMaxLength(300).IsRequired();
            entity.Property(r => r.Description).HasMaxLength(4000).IsRequired();
            entity.Property(r => r.Remediation).HasMaxLength(4000);
            entity.Property(r => r.LocalizationsJson).HasColumnType("jsonb").IsRequired();
            entity.Ignore(r => r.MajorVersion);
        });

        modelBuilder.Entity<Finding>(entity =>
        {
            entity.ToTable("findings");
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Id).ValueGeneratedNever();
            entity.Property(f => f.Fingerprint).HasMaxLength(64).IsRequired();
            entity.Property(f => f.RuleId).HasMaxLength(200).IsRequired();
            entity.Property(f => f.Category).HasConversion<string>().HasMaxLength(30).IsRequired();
            // Severity kept numeric so ordering in SQL follows the scale, not the alphabet.
            entity.Property(f => f.Severity).HasConversion<int>().IsRequired();
            entity.Property(f => f.Title).HasMaxLength(500).IsRequired();
            entity.Property(f => f.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(f => f.Origin).HasConversion<string>().HasMaxLength(20).IsRequired();

            entity.HasOne<Tenant>().WithMany().HasForeignKey(f => f.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(f => f.AssessmentId);
            entity.HasOne<RuleDefinition>().WithMany().HasForeignKey(f => f.RuleId);
            entity.HasIndex(f => new { f.TenantId, f.AssessmentId, f.Fingerprint }).IsUnique();
            entity.HasIndex(f => new { f.AssessmentId, f.RuleId, f.Status });
        });

        modelBuilder.Entity<FindingOccurrence>(entity =>
        {
            entity.ToTable("finding_occurrences");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Id).ValueGeneratedNever();
            entity.Property(o => o.Severity).HasConversion<int>().IsRequired();
            entity.Property(o => o.Confidence).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(o => o.Message).HasMaxLength(4000).IsRequired();
            entity.Property(o => o.Remediation).HasMaxLength(4000);
            entity.Property(o => o.DataJson).HasColumnType("jsonb");

            entity.OwnsOne(o => o.Evidence, evidence =>
            {
                evidence.Property(e => e.FilePath).HasMaxLength(1000);
                evidence.Property(e => e.Symbol).HasMaxLength(1000);
                evidence.Property(e => e.SnippetHash).HasMaxLength(64);
                evidence.Property(e => e.ScannerId).HasMaxLength(100).IsRequired();
                evidence.Property(e => e.ScannerVersion).HasMaxLength(50).IsRequired();
            });

            entity.HasOne<Tenant>().WithMany().HasForeignKey(o => o.TenantId);
            entity.HasOne<Finding>().WithMany().HasForeignKey(o => o.FindingId);
            entity.HasOne<Scan>().WithMany().HasForeignKey(o => o.ScanId);
            entity.HasIndex(o => o.ScanId);
            entity.HasIndex(o => new { o.FindingId, o.CreatedAtUtc });
        });

        modelBuilder.Entity<FindingSuppression>(entity =>
        {
            entity.ToTable("finding_suppressions");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.Fingerprint).HasMaxLength(64).IsRequired();
            entity.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(s => s.Reason).HasMaxLength(2000).IsRequired();
            entity.Property(s => s.Author).HasMaxLength(200).IsRequired();
            entity.Property(s => s.RevokedBy).HasMaxLength(200);
            entity.Ignore(s => s.IsActive);

            entity.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(s => s.AssessmentId);
            entity.HasOne<Finding>().WithMany().HasForeignKey(s => s.FindingId);
            entity.HasIndex(s => new { s.FindingId, s.RevokedAtUtc });
            entity.HasIndex(s => new { s.AssessmentId, s.CreatedAtUtc });
        });

        modelBuilder.Entity<InventorySnapshot>(entity =>
        {
            entity.ToTable("inventory_snapshots");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.CommitSha).HasMaxLength(64);
            entity.Property(s => s.LanguageId).HasMaxLength(50).IsRequired();
            entity.Property(s => s.TierAchieved).HasMaxLength(40).IsRequired();
            entity.Property(s => s.ProjectsJson).HasColumnType("jsonb").IsRequired();

            entity.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(s => s.AssessmentId);
            entity.HasOne<AssessmentRun>().WithMany().HasForeignKey(s => s.RunId);
            entity.HasIndex(s => new { s.AssessmentId, s.LanguageId, s.CreatedAtUtc });
            entity.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<HealthSnapshot>(entity =>
        {
            entity.ToTable("health_snapshots");
            entity.HasKey(h => h.Id);
            entity.Property(h => h.Id).ValueGeneratedNever();
            entity.Property(h => h.CommitSha).HasMaxLength(64);
            entity.Property(h => h.ModelVersion).HasMaxLength(40).IsRequired();
            entity.Property(h => h.RiskLevel).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(h => h.DimensionsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(h => h.Explanation).HasMaxLength(2000).IsRequired();

            entity.HasOne<Tenant>().WithMany().HasForeignKey(h => h.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(h => h.AssessmentId);
            entity.HasOne<AssessmentRun>().WithMany().HasForeignKey(h => h.RunId);
            entity.HasIndex(h => new { h.AssessmentId, h.CreatedAtUtc });
            entity.HasIndex(h => h.RunId);
        });

        modelBuilder.Entity<ConnectorCredential>(entity =>
        {
            entity.ToTable("connector_credentials");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedNever();
            entity.Property(c => c.Name).HasMaxLength(ConnectorCredential.MaxNameLength).IsRequired();
            entity.Property(c => c.Username).HasMaxLength(200);
            entity.Property(c => c.Description).HasMaxLength(500);
            entity.Property(c => c.Envelope).IsRequired();

            entity.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId);
            entity.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
        });

        modelBuilder.Entity<SuppressionPolicy>(entity =>
        {
            entity.ToTable("suppression_policies");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.Property(p => p.RulePattern).HasMaxLength(200).IsRequired();
            entity.Property(p => p.PathGlob).HasMaxLength(500);
            entity.Property(p => p.Reason).HasMaxLength(2000).IsRequired();
            entity.Property(p => p.Author).HasMaxLength(200).IsRequired();

            entity.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(p => p.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(p => new { p.TenantId, p.AssessmentId });
        });

        modelBuilder.Entity<Atlas.Domain.Modernization.ModernizationActual>(entity =>
        {
            entity.ToTable("modernization_actuals");
            entity.HasKey(a => a.AssessmentId);
            entity.Property(a => a.AssessmentId).ValueGeneratedNever();
            entity.Property(a => a.Strategy).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.Property(a => a.ActualCost).HasColumnType("numeric(18,2)");
            entity.Property(a => a.Currency).HasMaxLength(8).IsRequired();
            entity.Property(a => a.Notes).HasMaxLength(4000);
            entity.Property(a => a.RecordedBy).HasMaxLength(200).IsRequired();

            entity.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId);
            entity.HasOne<Assessment>().WithOne().HasForeignKey<Atlas.Domain.Modernization.ModernizationActual>(a => a.AssessmentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Atlas.Domain.Ai.AiProviderSettings>(entity =>
        {
            entity.ToTable("ai_provider_settings");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedNever();
            entity.Property(a => a.Provider).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(a => a.Model).HasMaxLength(Atlas.Domain.Ai.AiProviderSettings.MaxModelLength).IsRequired();
            entity.Property(a => a.BaseUrl).HasMaxLength(Atlas.Domain.Ai.AiProviderSettings.MaxBaseUrlLength);
            entity.Property(a => a.LastTestMessage).HasMaxLength(500);
            entity.Ignore(a => a.HasKey);
            entity.Ignore(a => a.RequiresKey);
            entity.Ignore(a => a.IsUsable);

            entity.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId);
            entity.HasIndex(a => a.TenantId).IsUnique();
        });

        modelBuilder.Entity<Atlas.Domain.Ai.BusinessRuleAnalysis>(entity =>
        {
            entity.ToTable("business_rule_analyses");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedNever();
            entity.Property(a => a.Provider).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(a => a.Model).HasMaxLength(200).IsRequired();
            entity.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(a => a.Error).HasMaxLength(2000);

            entity.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(a => a.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(a => new { a.AssessmentId, a.StartedAtUtc });
        });

        modelBuilder.Entity<Atlas.Domain.Ai.BusinessRule>(entity =>
        {
            entity.ToTable("business_rules");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.FilePath).HasMaxLength(1000).IsRequired();
            entity.Property(r => r.Symbol).HasMaxLength(500).IsRequired();
            entity.Property(r => r.Name).HasMaxLength(200).IsRequired();
            entity.Property(r => r.DescriptionEn).HasMaxLength(2000).IsRequired();
            entity.Property(r => r.DescriptionPt).HasMaxLength(2000).IsRequired();
            entity.Property(r => r.Category).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(r => r.ConditionsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(r => r.Model).HasMaxLength(200).IsRequired();
            entity.Property(r => r.FeedbackComment).HasMaxLength(Atlas.Domain.Ai.BusinessRule.MaxFeedbackLength);
            entity.Property(r => r.RatedBy).HasMaxLength(200);

            entity.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(r => r.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Atlas.Domain.Ai.BusinessRuleAnalysis>().WithMany().HasForeignKey(r => r.AnalysisId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(r => r.AssessmentId);
        });

        modelBuilder.Entity<Atlas.Domain.Ai.AiNarrative>(entity =>
        {
            entity.ToTable("ai_narratives");
            entity.HasKey(n => n.Id);
            entity.Property(n => n.Id).ValueGeneratedNever();
            entity.Property(n => n.Kind).HasMaxLength(40).IsRequired();
            entity.Property(n => n.Key).HasMaxLength(200).IsRequired();
            entity.Property(n => n.Lang).HasMaxLength(10).IsRequired();
            entity.Property(n => n.Text).HasMaxLength(Atlas.Domain.Ai.AiNarrative.MaxTextLength).IsRequired();
            entity.Property(n => n.Model).HasMaxLength(200).IsRequired();
            entity.Property(n => n.FeedbackComment).HasMaxLength(Atlas.Domain.Ai.AiNarrative.MaxFeedbackLength);
            entity.Property(n => n.RatedBy).HasMaxLength(200);

            entity.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(n => n.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(n => new { n.AssessmentId, n.Kind, n.Key, n.Lang }).IsUnique();
        });

        modelBuilder.Entity<Atlas.Domain.Security.ApiToken>(entity =>
        {
            entity.ToTable("api_tokens");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).ValueGeneratedNever();
            entity.Property(t => t.Name).HasMaxLength(Atlas.Domain.Security.ApiToken.MaxNameLength).IsRequired();
            entity.Property(t => t.Hint).HasMaxLength(40).IsRequired();
            entity.Property(t => t.Hash).HasMaxLength(64).IsRequired();
            entity.Property(t => t.Role).HasMaxLength(20).IsRequired();
            entity.Property(t => t.CreatedBy).HasMaxLength(200).IsRequired();

            entity.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId);
            entity.HasIndex(t => t.Hash).IsUnique();
            entity.HasIndex(t => t.TenantId);
        });

        modelBuilder.Entity<Atlas.Domain.Rules.RuleSeverityOverride>(entity =>
        {
            entity.ToTable("rule_severity_overrides");
            entity.HasKey(o => new { o.TenantId, o.RuleId });
            entity.Property(o => o.RuleId).HasMaxLength(200);
            entity.Property(o => o.UpdatedBy).HasMaxLength(200);
        });

        modelBuilder.Entity<AssessmentAccess>(entity =>
        {
            entity.ToTable("assessment_access");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedNever();
            entity.Property(a => a.Subject).HasMaxLength(AssessmentAccess.MaxSubjectLength).IsRequired();
            entity.Property(a => a.SubjectName).HasMaxLength(200);
            entity.Property(a => a.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(a => a.GrantedBy).HasMaxLength(200).IsRequired();

            entity.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId);
            entity.HasOne<Assessment>().WithMany().HasForeignKey(a => a.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(a => new { a.AssessmentId, a.Subject }).IsUnique();
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("audit_entries");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedOnAdd();
            entity.Property(a => a.Actor).HasMaxLength(200).IsRequired();
            entity.Property(a => a.Method).HasMaxLength(10).IsRequired();
            entity.Property(a => a.Path).HasMaxLength(500).IsRequired();
            entity.Property(a => a.Detail).HasMaxLength(500);
            entity.Property(a => a.ClientIp).HasMaxLength(64);
            entity.HasIndex(a => new { a.TenantId, a.AtUtc });
            entity.HasIndex(a => a.AssessmentId);
        });
    }

    /// <summary>
    /// every tenant-scoped table is filtered by the current tenant. In
    /// system scope (null) nothing is filtered. The filter reads the context field
    /// at query time, so one DbContext instance per request/scope is required.
    /// </summary>
    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workspace>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        // Assessment visibility = tenant + sharing: open (no entries) or listed for the current subject; admins see all.
        modelBuilder.Entity<Assessment>().HasQueryFilter(e =>
            (_tenant.TenantId == null || e.TenantId == _tenant.TenantId)
            && (_tenant.IsAdmin
                || !Set<AssessmentAccess>().Any(a => a.AssessmentId == e.Id)
                || Set<AssessmentAccess>().Any(a => a.AssessmentId == e.Id && a.Subject == _tenant.Subject)));
        modelBuilder.Entity<AssessmentRun>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<Scan>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<ScanJob>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<Finding>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<FindingOccurrence>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<FindingSuppression>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<Atlas.Domain.Rules.RuleSeverityOverride>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<InventorySnapshot>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<HealthSnapshot>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<ConnectorCredential>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<SuppressionPolicy>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<AuditEntry>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<Atlas.Domain.Ai.AiProviderSettings>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<Atlas.Domain.Ai.BusinessRule>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<Atlas.Domain.Ai.BusinessRuleAnalysis>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<Atlas.Domain.Ai.AiNarrative>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<Atlas.Domain.Modernization.ModernizationActual>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<Atlas.Domain.Security.ApiToken>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
        modelBuilder.Entity<AssessmentAccess>().HasQueryFilter(e => _tenant.TenantId == null || e.TenantId == _tenant.TenantId);
    }
}
