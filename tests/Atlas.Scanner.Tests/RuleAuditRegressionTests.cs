using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Dependencies.Vulnerabilities;
using Atlas.Scanner.Infrastructure;
using Atlas.Scanner.Licenses;

namespace Atlas.Scanner.Tests;

/// <summary>Regressions for the 2026-08 rule audit: each case reproduces a wrong verdict that was fixed.</summary>
public class RuleAuditRegressionTests
{
    // ---------- Licenses ----------

    [Fact]
    public void And_with_an_unknown_license_is_unknown_not_permissive()
    {
        Assert.Equal(LicenseClass.Unknown, LicenseClassifier.Classify("MIT AND LicenseRef-MyCorp-1.0"));
        Assert.Equal(LicenseClass.StrongCopyleft, LicenseClassifier.Classify("MIT AND GPL-3.0"));
    }

    [Fact]
    public void Lowercase_spdx_operators_still_split()
    {
        Assert.Equal(LicenseClass.Permissive, LicenseClassifier.Classify("GPL-2.0 or MIT"));
        Assert.Equal(LicenseClass.StrongCopyleft, LicenseClassifier.Classify("MIT and GPL-2.0"));
    }

    // ---------- CVSS vectors ----------

    [Fact]
    public void Cvss31_vector_of_a_critical_scores_as_critical_not_medium()
    {
        Assert.Equal(9.8, CvssVector.BaseScore("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H"));
        Assert.Equal(Severity.Critical, CvssVector.ToSeverity("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H"));
        Assert.Equal(Severity.Medium, CvssVector.ToSeverity("CVSS:3.0/AV:L/AC:L/PR:L/UI:N/S:U/C:H/I:N/A:N")!);
        Assert.Null(CvssVector.ToSeverity("not a vector"));
    }

    [Fact]
    public void Cvss31_scope_changed_formula_matches_the_spec()
    {
        // CVE-2017-5638-style vector: 10.0 with changed scope.
        Assert.Equal(10.0, CvssVector.BaseScore("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:C/C:H/I:H/A:H"));
    }

    // ---------- Infrastructure: Dockerfile rules ----------

    private sealed class Sink : IFindingSink
    {
        public List<FindingCandidate> Items { get; } = [];

        public void Emit(FindingCandidate candidate) => Items.Add(candidate);
    }

    private sealed class MemoryReader(Dictionary<string, string> files) : IArtifactReader
    {
        public string RootPath => "/mem";

        public IEnumerable<string> EnumerateFiles(string searchPattern) =>
            files.Keys.Where(f => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(f), ignoreCase: true));

        public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(files[relativePath]);

        public Stream OpenRead(string relativePath) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(files[relativePath]));
    }

    private static ScanContext Context(Dictionary<string, string> files, Sink sink) => new()
    {
        AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "r", Workspace = new MemoryReader(files),
        Languages = new Dictionary<string, LanguageAnalysisResult>(), Findings = sink, Today = new DateOnly(2026, 8, 30),
    };

    private static async Task<Sink> ScanDockerfileAsync(string dockerfile)
    {
        var sink = new Sink();
        await new InfrastructureScanner().ExecuteAsync(Context(new() { ["Dockerfile"] = dockerfile }, sink), CancellationToken.None);
        return sink;
    }

    [Fact]
    public async Task A_from_referencing_an_earlier_stage_is_not_an_unpinned_image()
    {
        var sink = await ScanDockerfileAsync("""
            FROM node:22.12 AS base
            RUN npm ci
            FROM base
            USER app
            ENTRYPOINT ["node", "app.js"]
            """);

        Assert.DoesNotContain(sink.Items, c => c.RuleId == InfrastructureScanner.RuleIds.UnpinnedBase);
    }

    [Fact]
    public async Task Node_18_and_20_and_dotnet_9_are_end_of_life_in_the_2026_catalog()
    {
        var sink = await ScanDockerfileAsync("""
            FROM node:20 AS build
            FROM mcr.microsoft.com/dotnet/aspnet:9.0
            USER app
            """);

        Assert.Equal(2, sink.Items.Count(c => c.RuleId == InfrastructureScanner.RuleIds.EolBase));
    }

    [Fact]
    public async Task Bare_password_env_is_flagged_and_benign_token_names_are_not()
    {
        var sink = await ScanDockerfileAsync("""
            FROM node:22.12
            ENV PASSWORD=hunter2secret
            ENV APP_NAME=shop DB_PASSWORD=hunter2secret
            ENV TOKEN_ENDPOINT=https://auth.example.com/token
            ENV JWT_TOKEN_LIFETIME_MINUTES=60
            ENV DB_PASSWORD_FILE=/run/secrets/db
            USER app
            """);

        var secrets = sink.Items.Where(c => c.RuleId == InfrastructureScanner.RuleIds.SecretInEnv).Select(c => c.Data!["name"]).ToList();
        Assert.Contains("PASSWORD", secrets);
        Assert.Contains("DB_PASSWORD", secrets); // second assignment on a multi-assignment line
        Assert.Equal(2, secrets.Count); // endpoints, knobs and file pointers are not secrets
    }
}
