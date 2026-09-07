using Atlas.Domain.Findings;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Runtime;
using Atlas.Scanner.Security;

namespace Atlas.Scanner.Tests.Security;

public class SecurityScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-security").FullName;

    public SecurityScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Web"));
        File.WriteAllText(Path.Combine(_root, "Web", "Web.config"), """
            <?xml version="1.0"?>
            <configuration>
              <system.web>
                <compilation debug="true" targetFramework="4.5" />
                <customErrors mode="Off" />
                <httpCookies httpOnlyCookies="false" />
              </system.web>
            </configuration>
            """);
    }

    private async Task<IReadOnlyList<FindingCandidate>> ScanAsync(params PatternFact[] patterns)
    {
        var language = new LanguageAnalysisResult(
            "csharp", AnalysisTier.Syntactic, [], [], [], new LanguageTotals(0, 0, 0, 0, 0, 0), null, patterns, [], [], []);
        var sink = new InMemoryFindingSink();

        var result = await new SecurityScanner().ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(),
            ScanId = Guid.NewGuid(),
            RepositoryKey = "repo",
            Workspace = new ContainedArtifactReader(_root),
            Languages = new Dictionary<string, LanguageAnalysisResult> { ["csharp"] = language },
            Findings = sink,
            Today = new DateOnly(2026, 8, 28),
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        return sink.Candidates;
    }

    [Fact]
    public async Task Maps_pattern_facts_to_declared_rules_with_member_symbols()
    {
        var candidates = await ScanAsync(
            new PatternFact(SecurityPatternIds.BinaryFormatter, "src/Ser.cs", 12, "Ser.Load", "new BinaryFormatter()"),
            new PatternFact(SecurityPatternIds.WeakHash, "src/Hash.cs", 3, "Hash.Of", "MD5.Create()"));

        var declared = new SecurityScanner().Rules.Select(r => r.Id).ToHashSet();
        Assert.All(candidates, c => Assert.Contains(c.RuleId, declared));

        var bf = Assert.Single(candidates, c => c.RuleId == SecurityPatternIds.BinaryFormatter);
        Assert.Equal(Severity.High, bf.Severity);
        Assert.Equal("Ser.Load", bf.Evidence.Symbol);
        Assert.Equal(12, bf.Evidence.LineStart);
        Assert.Contains("MD5.Create()", Assert.Single(candidates, c => c.RuleId == SecurityPatternIds.WeakHash).Message);
    }

    [Fact]
    public async Task Flags_insecure_web_config_settings_with_line_numbers()
    {
        var candidates = await ScanAsync();
        var config = candidates.Where(c => c.RuleId.StartsWith("sec.config.")).ToList();

        Assert.Equal(3, config.Count);
        Assert.Contains(config, c => c.RuleId == SecurityScanner.ConfigRuleIds.DebugEnabled);
        Assert.Contains(config, c => c.RuleId == SecurityScanner.ConfigRuleIds.CustomErrorsOff);
        Assert.Contains(config, c => c.RuleId == SecurityScanner.ConfigRuleIds.CookiesNotHttpOnly);
        Assert.All(config, c => Assert.True(c.Evidence.LineStart > 0));
    }

    [Fact]
    public async Task Ignores_unknown_pattern_ids_instead_of_emitting_undeclared_rules()
    {
        var candidates = await ScanAsync(new PatternFact("sec.future.pattern", "a.cs", 1, "A.B", "x"));

        Assert.DoesNotContain(candidates, c => c.RuleId == "sec.future.pattern");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
