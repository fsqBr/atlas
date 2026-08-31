using Atlas.Domain.Findings;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Privacy;

namespace Atlas.Scanner.Tests.Privacy;

public class PrivacyScannerTests
{
    private sealed class Sink : IFindingSink
    {
        public List<FindingCandidate> Items { get; } = [];

        public void Emit(FindingCandidate candidate) => Items.Add(candidate);
    }

    private static LanguageAnalysisResult WithPatterns(params PatternFact[] patterns) => new(
        "csharp", AnalysisTier.Syntactic, [], [], [], new LanguageTotals(1, 10, 1, 1, 1, 1), null, patterns, [], [], []);

    [Fact]
    public async Task Aggregates_fields_per_type_and_category_and_escalates_sensitive_leaks()
    {
        var scanner = new PrivacyScanner();
        var sink = new Sink();
        var languages = new Dictionary<string, LanguageAnalysisResult>
        {
            ["csharp"] = WithPatterns(
                new PatternFact("pii.field.identifier", "Customer.cs", 8, "Customer.Cpf", "Cpf : string"),
                new PatternFact("pii.field.identifier", "Customer.cs", 9, "Customer.Rg", "Rg : string"),
                new PatternFact("pii.field.contact", "Customer.cs", 10, "Customer.Email", "Email : string"),
                new PatternFact("pii.field.contact", "Customer.cs", 11, "Customer.Telefone", "Telefone : string"),
                new PatternFact("pii.field.contact", "Customer.cs", 12, "Customer.Cep", "Cep : string"),
                new PatternFact("pii.field.health", "Patient.cs", 12, "Patient.Diagnostico", "Diagnostico : string"),
                new PatternFact(PrivacyPatternIds.LeakToLog, "Service.cs", 30, "Service.Register", "_logger.LogInformation ← Cpf [identifier]"),
                new PatternFact(PrivacyPatternIds.LeakToLog, "Service.cs", 31, "Service.Register", "Console.WriteLine ← senha [credential]"),
                new PatternFact(PrivacyPatternIds.LeakToException, "Service.cs", 33, "Service.Register", "new ArgumentException(…) ← Cpf [identifier]"),
                new PatternFact("sec.sql.string-concatenation", "Repo.cs", 5, "Repo.Load", "ignored by this scanner")),
        };

        var result = await scanner.ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "r",
            Workspace = new EmptyReader(), Languages = languages, Findings = sink, Today = new DateOnly(2026, 8, 29),
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.All(sink.Items, c => Assert.True(scanner.Rules.Any(r => r.Id == c.RuleId), c.RuleId));

        // 6 field facts → 3 aggregated findings (Customer#identifier, Customer#contact, Patient#health) + 3 leaks.
        Assert.Equal(6, sink.Items.Count);

        var identifiers = sink.Items.Single(c => c.RuleId == PrivacyScanner.RuleIds.Identifier);
        Assert.Equal("Customer#identifier", identifiers.Evidence.Symbol);
        Assert.Equal("Cpf, Rg", identifiers.Data!["members"]);
        Assert.Equal("2", identifiers.Data["count"]);
        Assert.Equal(8, identifiers.Evidence.LineStart);
        Assert.Equal(Severity.Medium, identifiers.Severity);

        var contact = sink.Items.Single(c => c.RuleId == PrivacyScanner.RuleIds.Contact);
        Assert.Equal("3", contact.Data!["count"]);
        Assert.Equal("Customer: 3 contact field(s)", contact.Title);

        Assert.Equal(Severity.High, sink.Items.Single(c => c.RuleId == PrivacyScanner.RuleIds.Health).Severity);

        var logLeaks = sink.Items.Where(c => c.RuleId == PrivacyScanner.RuleIds.LeakToLog).ToList();
        Assert.Equal(Severity.High, logLeaks.Single(l => l.Data!["dataCategory"] == "identifier").Severity);
        Assert.Equal(Severity.Critical, logLeaks.Single(l => l.Data!["dataCategory"] == "credential").Severity);
        Assert.Equal(Severity.Medium, sink.Items.Single(c => c.RuleId == PrivacyScanner.RuleIds.LeakToException).Severity);
    }

    [Fact]
    public void Every_rule_is_bilingual_and_in_the_data_category()
    {
        var scanner = new PrivacyScanner();
        Assert.Equal(8, scanner.Rules.Count);
        Assert.All(scanner.Rules, r =>
        {
            Assert.Equal(FindingCategory.Data, r.Category);
            Assert.NotNull(r.Localizations);
            Assert.True(r.Localizations!.ContainsKey("pt-BR"));
            Assert.NotNull(r.Localizations["pt-BR"].MessageTemplate);
        });
    }

    private sealed class EmptyReader : Atlas.Domain.Workspaces.IArtifactReader
    {
        public string RootPath => "/none";

        public IEnumerable<string> EnumerateFiles(string searchPattern) => [];

        public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Stream OpenRead(string relativePath) => Stream.Null;
    }
}
