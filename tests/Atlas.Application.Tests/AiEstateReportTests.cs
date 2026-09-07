using System.Text.Json;
using Atlas.Application.AiEstate;
using Atlas.Application.Portfolio;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Health;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;
using Atlas.Reporting;

namespace Atlas.Application.Tests;

public class AiEstateReportTests
{
    private const string Hostile = "<script>alert('xss')</script>";

    private static string EstateJson(bool allowlist = true, bool remoteMcp = true) => JsonSerializer.Serialize(new
    {
        catalog = new { version = "1.0.0", hash = new string('a', 64), source = "builtin" },
        providers = new object[]
        {
            new { id = "openai", name = $"OpenAI {Hostile}", kind = "external-api", packages = new[] { "npm:openai" }, endpointFiles = 2, envVars = new[] { "OPENAI_API_KEY" }, models = new[] { "gpt-4o" }, codeFiles = 3, approved = allowlist ? false : (bool?)null },
            new { id = "azure-openai", name = "Azure OpenAI", kind = "external-api", packages = new[] { "nuget:Azure.AI.OpenAI" }, endpointFiles = 0, envVars = Array.Empty<string>(), models = Array.Empty<string>(), codeFiles = 1, approved = allowlist ? true : (bool?)null },
            new { id = "ollama", name = "Ollama", kind = "local-runtime", packages = Array.Empty<string>(), endpointFiles = 1, envVars = Array.Empty<string>(), models = new[] { "llama3.2" }, codeFiles = 0, approved = allowlist ? true : (bool?)null },
        },
        frameworks = new object[] { new { name = "Semantic Kernel", packages = new[] { "nuget:Microsoft.SemanticKernel" } } },
        mcp = new object[]
        {
            new { config = ".mcp.json", name = $"fs {Hostile}", transport = "stdio", capability = "ReadWrite", remote = false, host = (string?)null, command = "npx", known = "Filesystem", secrets = 1 },
            new { config = ".mcp.json", name = "docs", transport = "http", capability = "Read", remote = remoteMcp, host = remoteMcp ? "mcp.context7.com" : "localhost:3001", command = (string?)null, known = "Context7 docs", secrets = 0 },
        },
        models = new { active = new[] { "gpt-4o", "llama3.2" }, retired = new object[] { new { model = "gpt-4-32k", retiredOn = "2025-06-06", replacement = "gpt-4.1", files = 2 } } },
        vectorStores = Array.Empty<string>(),
        localRuntimes = new[] { "ollama" },
        gateways = Array.Empty<string>(),
        allowlist = new { configured = allowlist, approved = allowlist ? new[] { "azure-openai" } : null, unapproved = allowlist ? new[] { "openai" } : Array.Empty<string>() },
        secretsInMcp = 1,
    });

    private static string OccurrenceData(string estateJson) => JsonSerializer.Serialize(new Dictionary<string, string> { ["providers"] = "3", ["estateJson"] = estateJson });

    [Fact]
    public void Record_parses_the_persisted_inventory_and_tolerates_garbage()
    {
        var record = AiEstateRecord.Parse(OccurrenceData(EstateJson()));
        Assert.NotNull(record);
        Assert.Equal("1.0.0", record.CatalogVersion);
        Assert.Equal(3, record.Providers.Count);
        Assert.Equal(2, record.ExternalProviders.Count());
        Assert.Equal(2, record.SdkPackages);
        Assert.Equal(1, record.RemoteMcp);
        Assert.Single(record.RetiredModels);
        Assert.Equal("gpt-4.1", record.RetiredModels[0].Replacement);
        Assert.True(record.AllowlistConfigured);
        Assert.Equal(["openai"], record.Unapproved);
        Assert.False(record.Providers[0].Approved);
        Assert.Equal(1, record.SecretsInMcp);

        Assert.Null(AiEstateRecord.Parse(null));
        Assert.Null(AiEstateRecord.Parse("not json"));
        Assert.Null(AiEstateRecord.Parse("""{ "providers": "3" }"""));
        var minimal = AiEstateRecord.FromEstateJson("{}");
        Assert.NotNull(minimal);
        Assert.Empty(minimal.Providers);
        Assert.False(minimal.AllowlistConfigured);
    }

    [Fact]
    public void Portfolio_estate_folds_rows_and_correlates_with_pii_and_secrets()
    {
        var a = new Assessment(Guid.NewGuid(), WellKnownTenants.DefaultId, "Billing", new SourceReference("local", "/a"));
        var b = new Assessment(Guid.NewGuid(), WellKnownTenants.DefaultId, "Portal", new SourceReference("local", "/b"));
        var c = new Assessment(Guid.NewGuid(), WellKnownTenants.DefaultId, "NoAi", new SourceReference("local", "/c"));
        a.SetTags(["payments"]);

        var inventories = new Dictionary<Guid, string?>
        {
            [a.Id] = OccurrenceData(EstateJson()),
            [b.Id] = OccurrenceData(EstateJson(allowlist: false, remoteMcp: false)),
            [c.Id] = OccurrenceData("{}"),
        };
        var open = new List<OpenFindingSummary>
        {
            new(a.Id, "privacy.pii.contact", FindingCategory.Data, Severity.Medium, 4),
            new(a.Id, "secrets.openai-api-key", FindingCategory.Secrets, Severity.High, 1),
            new(a.Id, "ai.mcp.secret-in-config", FindingCategory.Secrets, Severity.High, 1), // an AI rule: not "leaked secrets" evidence by itself
            new(b.Id, "ai.mcp.secret-in-config", FindingCategory.Secrets, Severity.High, 1),
            new(b.Id, "quality.file.large", FindingCategory.Quality, Severity.Low, 9),
        };

        var estate = PortfolioAiEstate.Build([a, b, c], inventories, open);

        Assert.Equal(2, estate.AssessmentsWithAi);
        Assert.Equal(3, estate.AssessmentsScanned);
        Assert.Equal(["azure-openai", "ollama", "openai"], estate.Providers.Select(p => p.Id).ToList()); // ties broken by display name
        Assert.All(estate.Providers, p => Assert.Equal(2, p.Assessments));
        Assert.Equal([("Semantic Kernel", 2)], estate.Frameworks);
        Assert.Equal(4, estate.McpServers);
        Assert.Equal(1, estate.RemoteMcpServers);
        Assert.Equal(2, estate.SecretsInMcp);
        Assert.Equal(2, estate.RetiredModelReferences);
        Assert.True(estate.AllowlistConfigured);
        Assert.Equal([("openai", 1)], estate.Unapproved);
        Assert.Equal(1, estate.AiWithPii);
        Assert.Equal(1, estate.AiWithSecrets);
        Assert.Equal("1.0.0", estate.CatalogVersion);

        var billing = estate.Rows.Single(r => r.Name == "Billing");
        Assert.True(billing.PiiCoLocated);
        Assert.True(billing.SecretsCoLocated);
        Assert.Equal(1, billing.Unapproved);
        Assert.Equal(["payments"], billing.Tags);
        var portal = estate.Rows.Single(r => r.Name == "Portal");
        Assert.False(portal.PiiCoLocated);
        Assert.False(portal.SecretsCoLocated);
        Assert.Equal(0, portal.Unapproved);
    }

    private static ExecutiveReport Report(ReportAiEstate? ai, bool aiScan = true) => new(
        Header: new ReportHeader("Acme", null, "Billing", "git", "https://example.test/acme/billing.git", "main", "abc123", "Completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        Scans: aiScan ? [new ReportScan("ai.estate", "0.1.0", "Succeeded", 5, 5, 0, 0, 0, null)] : [new ReportScan("dependency.nuget", "0.1.0", "Succeeded", 1, 1, 0, 0, 0, null)],
        Inventory: [],
        Totals: new ReportTotals(0, 0, 0, Enum.GetValues<Severity>().ToDictionary(s => s, _ => 0), Enum.GetValues<FindingCategory>().ToDictionary(c => c, _ => 0)),
        RuleGroups: [],
        Findings: [],
        Health: null,
        AiEstate: ai);

    [Fact]
    public void Executive_report_renders_the_ai_estate_section_encoded_with_correlation()
    {
        var record = AiEstateRecord.FromEstateJson(EstateJson())!;
        var groups = new List<ReportRuleGroup> { new("ai.unapproved-provider", $"Unapproved {Hostile}", FindingCategory.Security, Severity.High, 1, null, ["web/package.json"]) };
        var html = HtmlReportRenderer.Render(Report(new ReportAiEstate(true, record, OpenPii: 4, OpenSecrets: 1, groups)));

        Assert.Contains("AI estate", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Correlation.", html);
        Assert.Contains("2 external AI provider(s)", html);
        Assert.Contains("mcp.context7.com", html);
        Assert.Contains("gpt-4-32k", html);
        Assert.Contains("NOT approved", html);
        Assert.Contains("Signature catalog 1.0.0", html);
        Assert.Contains("ai.unapproved-provider", html);

        var pt = HtmlReportRenderer.Render(Report(new ReportAiEstate(true, record, 0, 0, [])), ReportLocale.PtBr);
        Assert.Contains("Nenhum finding aberto de dados pessoais", pt);
        Assert.Contains("NÃO aprovado", pt);
    }

    [Fact]
    public void Executive_report_explains_when_the_scanner_did_not_run_or_found_nothing()
    {
        var notScanned = HtmlReportRenderer.Render(Report(new ReportAiEstate(false, null, 0, 0, []), aiScan: false));
        Assert.Contains("has not run on this assessment", notScanned);

        var legacy = HtmlReportRenderer.Render(Report(null, aiScan: false));
        Assert.Contains("has not run on this assessment", legacy);

        var nothing = HtmlReportRenderer.Render(Report(new ReportAiEstate(true, null, 0, 0, [])));
        Assert.Contains("No AI providers, SDKs, agent frameworks or MCP servers were detected", nothing);
    }

    [Fact]
    public void Portfolio_report_renders_the_ai_estate_section()
    {
        var a = new Assessment(Guid.NewGuid(), WellKnownTenants.DefaultId, $"Billing {Hostile}", new SourceReference("local", "/a"));
        var estate = PortfolioAiEstate.Build([a], new Dictionary<Guid, string?> { [a.Id] = OccurrenceData(EstateJson()) }, [new OpenFindingSummary(a.Id, "privacy.pii.contact", FindingCategory.Data, Severity.Medium, 2)]);
        var summary = new PortfolioSummary(1, 1, 70, Enum.GetValues<RiskLevel>().ToDictionary(r => r, _ => 0), 100, 10, 1, 0, 1, 0, [], 3,
            Enum.GetValues<Severity>().ToDictionary(s => s, _ => 0), Enum.GetValues<FindingCategory>().ToDictionary(c => c, _ => 0), [], [], null, null, estate);
        var report = new PortfolioReport("Acme", null, null, DateTimeOffset.UtcNow, summary, []);

        var html = PortfolioHtmlRenderer.Render(report);
        Assert.Contains("AI estate across the portfolio", html);
        Assert.Contains("Repositories using AI", html);
        Assert.Contains("PII co-located", html);
        Assert.Contains("unapproved provider", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);

        var empty = PortfolioHtmlRenderer.Render(report with { Summary = summary with { AiEstate = null } });
        Assert.Contains("No AI usage was inventoried yet", empty);
    }
}
