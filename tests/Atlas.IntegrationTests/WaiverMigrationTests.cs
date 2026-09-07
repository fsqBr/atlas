extern alias AtlasApi;

using System.Net;
using System.Net.Http.Json;
using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Domain.Findings;
using Atlas.Domain.Sources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>
/// Waiver migration after a rule major bump: the superseded finding keeps its waiver,
/// the successor starts Open, and only an explicit human request re-issues the waiver against it.
/// </summary>
public sealed class WaiverMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _source = Directory.CreateTempSubdirectory("atlas-it-waiver").FullName;

    private WebApplicationFactory<ApiProgram> Factory() => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:AtlasDb", fixture.ConnectionString);
        builder.UseSetting("Atlas:AutoMigrate", "false");
        builder.UseSetting("Atlas:Vulnerabilities:SyncEnabled", "false");
        builder.UseSetting("Atlas:Secrets:HmacKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Secrets:MasterKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Operations:RateLimitPerMinute", "1000");
    });

    private sealed record Migratable(Guid FindingId, string RuleId, string PredecessorFingerprint, string WaiverKind, string WaiverReason, string WaiverAuthor, DateTimeOffset? WaiverExpiresAtUtc);

    private sealed record MigrationResult(int Migrated, int Skipped, List<Guid> MigratedFindingIds);

    [Fact]
    public async Task Superseded_waiver_is_listed_and_migrates_only_on_explicit_request()
    {
        WriteLegacyProject();
        await using var provider = fixture.BuildServices();

        Guid assessmentId;
        using (var scope = provider.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<CreateAssessmentHandler>().HandleAsync(
                "Waiver estate", new SourceReference(SourceReference.Kinds.LocalFolder, _source), CancellationToken.None);
            assessmentId = created.AssessmentId;
        }

        await ClaimAndRunAsync(provider);

        // Plant the bump: waive an Open finding (v1 identity), then insert its v2 successor carrying the link.
        Guid supersededId, successorId;
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var successorFingerprint = new string('b', 64);
        using (var scope = provider.CreateScope())
        {
            var findingsRepo = scope.ServiceProvider.GetRequiredService<IFindingRepository>();
            var superseded = (await findingsRepo.ListAsync(assessmentId, 0, 100, CancellationToken.None,
                new FindingFilter(Status: FindingStatus.Open))).Items.First().Finding;
            supersededId = superseded.Id;

            await scope.ServiceProvider.GetRequiredService<TriageFindingHandler>().HandleAsync(
                assessmentId, superseded.Id, TriageAction.Suppress, "accepted risk until Q4", "Ana", CancellationToken.None, expiry);

            var successor = Finding.Create(
                Guid.NewGuid(), superseded.TenantId, assessmentId, successorFingerprint,
                superseded.RuleId, superseded.Category, superseded.Severity, superseded.Title,
                FindingOrigin.Deterministic, superseded.FirstSeenScanId, predecessorFingerprint: superseded.Fingerprint);
            successorId = successor.Id;
            findingsRepo.AddRange([successor]);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        using var factory = Factory();
        using var client = factory.CreateClient();

        // Unknown assessment → 404; the real one lists exactly the planted successor with its predecessor's waiver.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/assessments/{Guid.NewGuid()}/suppressions/migratable")).StatusCode);

        var listed = await client.GetFromJsonAsync<List<Migratable>>($"/api/assessments/{assessmentId}/suppressions/migratable");
        var item = Assert.Single(listed!);
        Assert.Equal(successorId, item.FindingId);
        Assert.Equal("Suppressed", item.WaiverKind);
        Assert.Equal("accepted risk until Q4", item.WaiverReason);
        Assert.Equal("Ana", item.WaiverAuthor);
        Assert.NotNull(item.WaiverExpiresAtUtc);

        // The AI estate tab endpoint: the scanner ran (fixture registers it), the legacy fixture has no AI → record null.
        var estate = await client.GetAsync($"/api/assessments/{assessmentId}/ai-estate");
        Assert.Equal(HttpStatusCode.OK, estate.StatusCode);
        var estateJson = await estate.Content.ReadAsStringAsync();
        Assert.Contains("\"scanned\":true", estateJson);
        Assert.Contains("\"aiRules\":[", estateJson);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/assessments/{Guid.NewGuid()}/ai-estate")).StatusCode);

        // Migration is a new auditable decision: it needs an author.
        var noAuthor = await client.PostAsJsonAsync($"/api/assessments/{assessmentId}/suppressions/migrate", new { findingIds = (Guid[]?)null, author = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noAuthor.StatusCode);

        // Nothing moved on its own: the successor is still Open.
        using (var scope = provider.CreateScope())
        {
            Assert.Equal(FindingStatus.Open, (await scope.ServiceProvider.GetRequiredService<IFindingRepository>().GetAsync(successorId, CancellationToken.None))!.Status);
        }

        // Explicit request: one migratable id plus one unknown → 1 migrated, 1 skipped (never guessed at).
        var response = await client.PostAsJsonAsync($"/api/assessments/{assessmentId}/suppressions/migrate",
            new { findingIds = new[] { successorId, Guid.NewGuid() }, author = "Bruno" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<MigrationResult>();
        Assert.Equal(1, result!.Migrated);
        Assert.Equal(1, result.Skipped);
        Assert.Equal([successorId], result.MigratedFindingIds);

        using (var scope = provider.CreateScope())
        {
            var findingsRepo = scope.ServiceProvider.GetRequiredService<IFindingRepository>();
            var suppressions = scope.ServiceProvider.GetRequiredService<ISuppressionRepository>();

            var successor = (await findingsRepo.GetAsync(successorId, CancellationToken.None))!;
            Assert.Equal(FindingStatus.Suppressed, successor.Status);

            var migrated = await suppressions.GetActiveAsync(successorId, CancellationToken.None);
            Assert.NotNull(migrated);
            Assert.Equal("Bruno", migrated.Author);
            Assert.Equal(SuppressionKind.Suppressed, migrated.Kind);
            Assert.StartsWith("accepted risk until Q4", migrated.Reason);
            Assert.Contains("waiver migrated from superseded finding", migrated.Reason);
            Assert.NotNull(migrated.ExpiresAtUtc);
            Assert.True(Math.Abs((migrated.ExpiresAtUtc!.Value - expiry).TotalSeconds) < 1, "the migrated waiver keeps the original expiry");

            // The predecessor's own decision is untouched: history stays honest.
            var original = await suppressions.GetActiveAsync(supersededId, CancellationToken.None);
            Assert.NotNull(original);
            Assert.Equal("Ana", original.Author);
            Assert.Equal(FindingStatus.Suppressed, (await findingsRepo.GetAsync(supersededId, CancellationToken.None))!.Status);

            // Health was recomputed as a triage snapshot (no run).
            var health = await scope.ServiceProvider.GetRequiredService<IHealthRepository>().GetLatestAsync(assessmentId, CancellationToken.None);
            Assert.NotNull(health);
            Assert.Null(health.RunId);

            // Once migrated the successor is triaged: nothing left to migrate, and a second call is a no-op.
            var handler = scope.ServiceProvider.GetRequiredService<WaiverMigrationHandler>();
            Assert.Empty((await handler.ListAsync(assessmentId, CancellationToken.None))!);
            var again = await handler.MigrateAsync(assessmentId, null, "Bruno", CancellationToken.None);
            Assert.Equal(0, again!.Migrated);
            Assert.Equal(0, again.Skipped);
            Assert.Null(await handler.ListAsync(Guid.NewGuid(), CancellationToken.None));
        }
    }

    private static async Task ClaimAndRunAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IScanJobQueue>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var job = await queue.ClaimAsync("test-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(job);
        job.Start();
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        await scope.ServiceProvider.GetRequiredService<AssessmentRunner>().RunAsync(job.AssessmentId, CancellationToken.None);

        job.Succeed();
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }

    private void WriteLegacyProject()
    {
        var dir = Path.Combine(_source, "Legacy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Legacy.csproj"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="12.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.5</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="System" />
                <Reference Include="System.Web" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "class Program { static void Main() { } }");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_source, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
