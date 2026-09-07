using System.Text;
using System.Text.Json;
using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Ai;
using Atlas.Scanner.Ai.Catalog;
using Atlas.Scanner.Ai.Manifests;
using Atlas.Scanner.Ai.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Scanner.Tests.Ai;

public class AiEstateScannerTests
{
    private sealed class Reader(Dictionary<string, string> files) : IArtifactReader
    {
        public string RootPath => "/mem";

        public IEnumerable<string> EnumerateFiles(string searchPattern)
        {
            if (searchPattern == "*")
            {
                return files.Keys;
            }

            var suffix = searchPattern.TrimStart('*');
            return files.Keys.Where(k => k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(k).Equals(searchPattern, StringComparison.OrdinalIgnoreCase));
        }

        public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(files[relativePath]);

        public Stream OpenRead(string relativePath) => new MemoryStream(Encoding.UTF8.GetBytes(files[relativePath]));
    }

    private sealed class Sink : IFindingSink
    {
        public List<FindingCandidate> Items { get; } = [];

        public void Emit(FindingCandidate candidate) => Items.Add(candidate);
    }

    private static readonly SignatureCatalog Catalog = SignatureCatalog.LoadBuiltin();

    private static async Task<List<FindingCandidate>> RunAsync(Dictionary<string, string> files, AiEstateOptions? options = null, IReadOnlyList<ProjectFact>? projects = null, Dictionary<string, string>? settings = null)
    {
        var sink = new Sink();
        var languages = new Dictionary<string, LanguageAnalysisResult>();
        if (projects is not null)
        {
            languages[LanguageIds.CSharp] = new LanguageAnalysisResult("csharp", AnalysisTier.Syntactic, [], projects, [], new LanguageTotals(0, 0, 0, 0, 0, 0), null, [], [], [], []);
        }

        var scanner = new AiEstateScanner(Catalog, options ?? new AiEstateOptions { HmacKeyBase64 = Convert.ToBase64String(new byte[32]) }, NullLogger<AiEstateScanner>.Instance);
        var result = await scanner.ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "repo", Workspace = new Reader(files),
            Languages = languages, Findings = sink, Today = new DateOnly(2026, 9, 6), Settings = settings ?? new Dictionary<string, string>(),
        }, CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
        return sink.Items;
    }

    // ---- catalog contract ----

    [Fact]
    public void Builtin_catalog_is_valid_versioned_and_hashed()
    {
        Assert.Empty(SignatureCatalog.Validate(Catalog.Document));
        Assert.Equal("1.0.0", Catalog.Version);
        Assert.Equal(64, Catalog.Hash.Length);
        Assert.Equal("builtin", Catalog.Source);
        Assert.True(Catalog.Document.Packages.Count >= 100, $"expected ≥100 package signatures, found {Catalog.Document.Packages.Count}");
        Assert.True(Catalog.Document.Endpoints.Count >= 20);
        Assert.True(Catalog.Document.Models.Count >= 20);
        Assert.All(Catalog.Document.Packages.Where(p => p.Provider is not null), p => Assert.NotNull(Catalog.Provider(p.Provider)));
    }

    [Fact]
    public void Rules_cover_every_rule_id_and_carry_portuguese_localization()
    {
        var scanner = new AiEstateScanner(Catalog, new AiEstateOptions(), NullLogger<AiEstateScanner>.Instance);
        var ids = typeof(AiEstateScanner.RuleIds).GetFields().Select(f => (string)f.GetValue(null)!).ToHashSet();
        Assert.Equal(ids, scanner.Rules.Select(r => r.Id).ToHashSet());
        Assert.All(scanner.Rules, r => Assert.True(r.Localizations!.ContainsKey("pt-BR"), r.Id));
        Assert.Equal("ai.estate", scanner.Descriptor.Id);
    }

    [Fact]
    public void Invalid_custom_catalog_falls_back_to_builtin()
    {
        var path = Path.Combine(Path.GetTempPath(), $"atlas-bad-catalog-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "version": "9.0.0", "providers": [], "packages": [], "endpoints": [ { "id": "x", "provider": "nope", "pattern": "(", "fixtures": [] } ], "models": [], "envVars": [], "mcp": { "configFiles": [".mcp.json"], "knownServers": [] } }""");
        try
        {
            var loaded = Atlas.Scanner.Ai.DependencyInjection.LoadCatalog(new AiEstateOptions { CatalogPath = path }, NullLogger.Instance);
            Assert.Equal("builtin", loaded.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Valid_custom_catalog_replaces_builtin_wholesale()
    {
        var path = Path.Combine(Path.GetTempPath(), $"atlas-catalog-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "version": "2.0.0",
              "providers": [ { "id": "acme-llm", "name": "Acme LLM", "kind": "external-api" } ],
              "packages": [ { "ecosystem": "npm", "name": "acme-llm", "provider": "acme-llm" } ],
              "endpoints": [ { "id": "acme", "provider": "acme-llm", "pattern": "api\\.acme-llm\\.example", "fixtures": ["https://api.acme-llm.example/v1"] } ],
              "models": [], "envVars": [], "mcp": { "configFiles": [".mcp.json"], "knownServers": [] } }
            """);
        try
        {
            var loaded = Atlas.Scanner.Ai.DependencyInjection.LoadCatalog(new AiEstateOptions { CatalogPath = path }, NullLogger.Instance);
            Assert.Equal("2.0.0", loaded.Version);
            Assert.Equal(path, loaded.Source);
            Assert.Null(loaded.MatchPackage("npm", "openai"));
            Assert.NotNull(loaded.MatchPackage("npm", "acme-llm"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- manifests ----

    [Fact]
    public void Manifest_parsers_extract_names_across_ecosystems()
    {
        var npm = PackageManifests.ParsePackageJson("web/package.json", """{ "dependencies": { "openai": "^4.0.0", "@langchain/core": "0.3.1" }, "devDependencies": { "vitest": "1" } }""");
        Assert.Equal(["openai", "@langchain/core", "vitest"], npm.Select(p => p.Name).ToList());

        var pip = PackageManifests.ParseRequirements("requirements.txt", "openai==1.40.0\nLangChain_Community>=0.2 # rag\n-r base.txt\ngit+https://x/y\nanthropic[bedrock]~=0.30\n");
        Assert.Equal(["openai", "langchain-community", "anthropic"], pip.Select(p => p.Name).ToList());
        Assert.Equal("1.40.0", pip[0].Version);

        var pyproject = PackageManifests.ParsePyProject("pyproject.toml", """
            [project]
            name = "svc"
            dependencies = [
              "openai>=1.0",
              "crewai==0.5.0",
            ]
            [tool.poetry.dependencies]
            python = "^3.11"
            langgraph = "^0.2"
            """);
        Assert.Equal(["openai", "crewai", "langgraph"], pyproject.Select(p => p.Name).ToList());

        var pom = PackageManifests.ParsePom("pom.xml", """
            <project xmlns="http://maven.apache.org/POM/4.0.0"><dependencies>
              <dependency><groupId>dev.langchain4j</groupId><artifactId>langchain4j-open-ai</artifactId><version>1.0.0</version></dependency>
              <dependency><groupId>org.junit</groupId><artifactId>junit</artifactId></dependency>
            </dependencies></project>
            """);
        Assert.Equal(["dev.langchain4j:langchain4j-open-ai", "org.junit:junit"], pom.Select(p => p.Name).ToList());

        var gradle = PackageManifests.ParseGradle("build.gradle.kts", "dependencies {\n implementation(\"org.springframework.ai:spring-ai-openai:1.0.0\")\n testImplementation('junit:junit:4.13')\n}");
        Assert.Equal(["org.springframework.ai:spring-ai-openai", "junit:junit"], gradle.Select(p => p.Name).ToList());

        Assert.Empty(PackageManifests.ParsePackageJson("p", "not json"));
        Assert.Empty(PackageManifests.ParsePom("p", "<broken"));
    }

    // ---- scanner ----

    [Fact]
    public async Task Repository_without_ai_yields_no_findings()
    {
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["src/App/Program.cs"] = "using System;\nclass P { static void Main() { Console.WriteLine(\"hi\"); } }",
            ["web/package.json"] = """{ "dependencies": { "react": "18" } }""",
        });
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Declared_sdks_and_frameworks_produce_inventory_with_providers()
    {
        var project = new ProjectFact("src/Bot/Bot.csproj", "Bot", true, "net8.0",
            [new PackageReferenceFact("Azure.AI.OpenAI", "2.1.0", PackageReferenceOrigin.PackageReference), new PackageReferenceFact("Microsoft.SemanticKernel", "1.30.0", PackageReferenceOrigin.PackageReference), new PackageReferenceFact("Serilog", "3.0", PackageReferenceOrigin.PackageReference)],
            [], []);
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["web/package.json"] = """{ "dependencies": { "openai": "^4.0.0", "@langchain/core": "0.3.1", "react": "18" } }""",
            ["ml/requirements.txt"] = "anthropic==0.34.0\ncrewai==0.5.0\nnumpy\n",
            ["svc/pom.xml"] = "<project><dependencies><dependency><groupId>dev.langchain4j</groupId><artifactId>langchain4j</artifactId><version>1.0.0</version></dependency></dependencies></project>",
        }, projects: [project]);

        var inventory = Assert.Single(findings, f => f.RuleId == AiEstateScanner.RuleIds.Inventory);
        Assert.Equal("3", inventory.Data!["providers"]); // azure-openai, openai, anthropic
        Assert.Equal("4", inventory.Data["frameworks"]); // Semantic Kernel, LangChain, CrewAI, LangChain4j
        Assert.Equal("1.0.0", inventory.Data["catalogVersion"]);
        Assert.Equal(Catalog.Hash[..16], inventory.Data["catalogHash"]);

        using var estate = JsonDocument.Parse(inventory.Data["estateJson"]);
        var providers = estate.RootElement.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Equal(["anthropic", "azure-openai", "openai"], providers);
        Assert.Equal(Catalog.Hash, estate.RootElement.GetProperty("catalog").GetProperty("hash").GetString());

        var sdks = findings.Where(f => f.RuleId == AiEstateScanner.RuleIds.ProviderSdk).ToList();
        Assert.Contains(sdks, f => f.Evidence.Symbol == "nuget:Azure.AI.OpenAI" && f.Evidence.FilePath == "src/Bot/Bot.csproj" && f.Data!["version"] == "2.1.0");
        Assert.Contains(sdks, f => f.Evidence.Symbol == "npm:openai");
        Assert.Contains(sdks, f => f.Evidence.Symbol == "pypi:anthropic" && f.Data!["provider"] == "Anthropic (Claude)");
        Assert.DoesNotContain(sdks, f => f.Evidence.Symbol!.Contains("Serilog") || f.Evidence.Symbol.Contains("react") || f.Evidence.Symbol.Contains("numpy"));

        var frameworks = findings.Where(f => f.RuleId == AiEstateScanner.RuleIds.AgentFramework).Select(f => f.Data!["framework"]).ToHashSet();
        Assert.Equal(["CrewAI", "LangChain", "LangChain4j", "Semantic Kernel"], frameworks.OrderBy(x => x, StringComparer.Ordinal).ToList());

        // No allowlist configured: the unapproved rule is silent.
        Assert.DoesNotContain(findings, f => f.RuleId == AiEstateScanner.RuleIds.UnapprovedProvider);
        Assert.Equal("false", inventory.Data["allowlistConfigured"]);
    }

    [Fact]
    public async Task Code_evidence_endpoints_models_env_vars_and_imports()
    {
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["src/Chat/Client.cs"] = """
                using Azure.AI.OpenAI;
                using System.Net.Http;
                class Client {
                    const string Url = "https://api.openai.com/v1/chat/completions";
                    const string Model = "gpt-4-32k";
                    string Key => Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                }
                """,
            ["scripts/local.py"] = "import ollama\nclient = ollama.Client(host='http://localhost:11434')\nresp = client.chat(model='llama3.2')\n",
            ["appsettings.json"] = """{ "Ai": { "Endpoint": "https://contoso-prod.openai.azure.com/", "Deployment": "gpt-4o" } }""",
        });

        var endpoint = Assert.Single(findings, f => f.RuleId == AiEstateScanner.RuleIds.DirectEndpoint && f.Evidence.FilePath == "src/Chat/Client.cs");
        Assert.Equal(Severity.Low, endpoint.Severity);
        Assert.Equal("api.openai.com", endpoint.Data!["host"]);
        Assert.Equal(4, endpoint.Evidence.LineStart);
        Assert.Contains(findings, f => f.RuleId == AiEstateScanner.RuleIds.DirectEndpoint && f.Evidence.FilePath == "appsettings.json" && f.Data!["providerId"] == "azure-openai");

        var retired = Assert.Single(findings, f => f.RuleId == AiEstateScanner.RuleIds.RetiredModel);
        Assert.Equal("gpt-4-32k", retired.Data!["model"]);
        Assert.Equal(Severity.Medium, retired.Severity);
        Assert.NotEqual("—", retired.Data["replacement"]);

        var local = Assert.Single(findings, f => f.RuleId == AiEstateScanner.RuleIds.LocalRuntime);
        Assert.Equal("ollama", local.Data!["providerId"]);
        Assert.Equal(Severity.Informational, local.Severity);

        // Import-only evidence (no manifest declares the SDK) still lands in the inventory, with medium confidence.
        var sdk = Assert.Single(findings, f => f.RuleId == AiEstateScanner.RuleIds.ProviderSdk && f.Evidence.Symbol == "nuget:Azure.AI.OpenAI");
        Assert.Equal("import", sdk.Data!["source"]);
        Assert.Equal(ConfidenceLevel.Medium, sdk.Confidence);
        Assert.Contains(findings, f => f.RuleId == AiEstateScanner.RuleIds.ProviderSdk && f.Evidence.Symbol == "pypi:ollama");

        var inventory = Assert.Single(findings, f => f.RuleId == AiEstateScanner.RuleIds.Inventory);
        using var estate = JsonDocument.Parse(inventory.Data!["estateJson"]);
        var openai = estate.RootElement.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "openai");
        Assert.Contains("OPENAI_API_KEY", openai.GetProperty("envVars").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("gpt-4-32k", openai.GetProperty("models").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("ollama", estate.RootElement.GetProperty("localRuntimes").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("1", inventory.Data["retiredModels"]);
    }

    [Fact]
    public async Task Mcp_configs_are_inventoried_classified_and_secrets_fingerprinted_never_stored()
    {
        const string token = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ab";
        var findings = await RunAsync(new Dictionary<string, string>
        {
            [".mcp.json"] = $$"""
                {
                  // Claude Code project config
                  "mcpServers": {
                    "fs": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "./src"] },
                    "github": { "command": "docker", "args": ["run", "-i", "ghcr.io/github/github-mcp-server"], "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "{{token}}" } },
                    "browser": { "command": "npx", "args": ["@playwright/mcp@latest"] },
                    "docs": { "type": "http", "url": "https://mcp.context7.com/mcp", "headers": { "Authorization": "Bearer ${CONTEXT7_KEY}" } },
                  }
                }
                """,
            [".vscode/mcp.json"] = """{ "servers": { "local-api": { "type": "sse", "url": "http://localhost:3001/sse" } } }""",
        });

        var servers = findings.Where(f => f.RuleId == AiEstateScanner.RuleIds.McpServer).ToList();
        Assert.Equal(5, servers.Count);
        Assert.Equal("ReadWrite", servers.Single(s => s.Data!["server"] == "fs").Data!["capability"]);
        Assert.Equal("Execute", servers.Single(s => s.Data!["server"] == "browser").Data!["capability"]);
        Assert.Equal("GitHub", servers.Single(s => s.Data!["server"] == "github").Data!["knownServerName"]);
        Assert.Equal("sse", servers.Single(s => s.Data!["server"] == "local-api").Data!["transport"]);

        var remote = Assert.Single(findings, f => f.RuleId == AiEstateScanner.RuleIds.McpRemoteServer);
        Assert.Equal("mcp.context7.com", remote.Data!["host"]);
        Assert.Equal(Severity.Medium, remote.Severity);

        var secret = Assert.Single(findings, f => f.RuleId == AiEstateScanner.RuleIds.McpSecretInConfig);
        Assert.Equal("env.GITHUB_PERSONAL_ACCESS_TOKEN", secret.Data!["location"]);
        Assert.Equal(32, secret.Data["fingerprint"].Length);
        Assert.Equal("ghp_…", secret.Data["preview"]);
        Assert.Equal(Severity.High, secret.Severity);

        // The credential value never appears anywhere in what leaves the scanner.
        var everything = string.Join("\n", findings.SelectMany(f => new[] { f.Title, f.Message, f.Remediation ?? string.Empty, f.Evidence.Symbol ?? string.Empty }.Concat(f.Data?.Values ?? [])));
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain(token[4..20], everything);
    }

    [Fact]
    public void Mcp_reader_ignores_placeholders_and_keeps_only_the_host_of_remote_urls()
    {
        var servers = McpConfigReader.Read("x/.cursor/mcp.json", """
            { "mcpServers": { "api": { "url": "https://tools.example.com:8443/mcp?token=sk-live-abcdefghijklmnopqrstuvwxyz", "env": { "KEY": "${MY_KEY}", "OTHER": "changeme" } } } }
            """, Catalog, null);
        var server = Assert.Single(servers);
        Assert.Equal("tools.example.com:8443", server.Host);
        Assert.True(server.IsRemote);
        Assert.Empty(server.Secrets);
        Assert.Equal(["KEY", "OTHER"], server.EnvKeys);
        Assert.Empty(McpConfigReader.Read("bad.json", "{ not json", Catalog, null));
    }

    [Fact]
    public async Task Allowlist_flags_external_providers_only()
    {
        var files = new Dictionary<string, string>
        {
            ["web/package.json"] = """{ "dependencies": { "openai": "^4.0.0", "@azure/openai": "2.0.0", "ollama": "0.5.0" } }""",
        };

        var findings = await RunAsync(files, new AiEstateOptions { ApprovedProviders = ["azure-openai", "Anthropic"] });
        var unapproved = Assert.Single(findings, f => f.RuleId == AiEstateScanner.RuleIds.UnapprovedProvider);
        Assert.Equal("openai", unapproved.Data!["providerId"]);
        Assert.Equal(Severity.High, unapproved.Severity);
        Assert.Equal("anthropic, azure-openai", unapproved.Data["approved"]);

        var inventory = Assert.Single(findings, f => f.RuleId == AiEstateScanner.RuleIds.Inventory);
        Assert.Equal("true", inventory.Data!["allowlistConfigured"]);
        Assert.Equal("1", inventory.Data["unapproved"]);

        var none = await RunAsync(files, new AiEstateOptions { ApprovedProviders = ["openai", "azure-openai"] });
        Assert.DoesNotContain(none, f => f.RuleId == AiEstateScanner.RuleIds.UnapprovedProvider);
    }

    [Fact]
    public async Task Tenant_allowlist_setting_wins_over_deployment_config()
    {
        var files = new Dictionary<string, string> { ["web/package.json"] = """{ "dependencies": { "openai": "^4.0.0", "@anthropic-ai/sdk": "0.30.0" } }""" };
        var config = new AiEstateOptions { ApprovedProviders = ["openai"] };

        // Config alone: anthropic is the unapproved one.
        var fromConfig = await RunAsync(files, config);
        Assert.Equal("anthropic", Assert.Single(fromConfig, f => f.RuleId == AiEstateScanner.RuleIds.UnapprovedProvider).Data!["providerId"]);

        // The tenant saved its own list in the UI: it replaces the config wholesale.
        var fromTenant = await RunAsync(files, config, settings: new Dictionary<string, string> { [ScanSettingKeys.AiApprovedProviders] = "Anthropic, azure-openai" });
        Assert.Equal("openai", Assert.Single(fromTenant, f => f.RuleId == AiEstateScanner.RuleIds.UnapprovedProvider).Data!["providerId"]);

        // An empty setting means "no allowlist" only when the config has none either.
        var blank = await RunAsync(files, new AiEstateOptions(), settings: new Dictionary<string, string> { [ScanSettingKeys.AiApprovedProviders] = "" });
        Assert.DoesNotContain(blank, f => f.RuleId == AiEstateScanner.RuleIds.UnapprovedProvider);
    }

    [Fact]
    public async Task Output_is_deterministic_and_binary_or_huge_files_are_skipped()
    {
        var files = new Dictionary<string, string>
        {
            ["b.py"] = "import anthropic\nclient = anthropic.Anthropic()\n",
            ["a.py"] = "from openai import OpenAI\n",
            ["c/package.json"] = """{ "dependencies": { "ai": "3.0.0" } }""",
            ["blob.json"] = "\0\0binary https://api.openai.com",
            ["huge.js"] = new string('x', 1_100_000) + " https://api.anthropic.com",
        };

        var first = (await RunAsync(files)).Select(f => (f.RuleId, f.Evidence.FilePath, f.Evidence.Symbol, f.Message)).ToList();
        var second = (await RunAsync(files)).Select(f => (f.RuleId, f.Evidence.FilePath, f.Evidence.Symbol, f.Message)).ToList();
        Assert.Equal(first, second);
        Assert.DoesNotContain(first, f => f.FilePath is "blob.json" or "huge.js");
        Assert.Contains(first, f => f.RuleId == AiEstateScanner.RuleIds.ProviderSdk && f.FilePath == "c/package.json" && f.Symbol == "npm:ai"); // Vercel AI SDK (abstraction)
        Assert.Contains(first, f => f.RuleId == AiEstateScanner.RuleIds.ProviderSdk && f.Symbol == "pypi:openai");
        Assert.Contains(first, f => f.RuleId == AiEstateScanner.RuleIds.ProviderSdk && f.Symbol == "pypi:anthropic");
    }

    [Theory]
    [InlineData("@scope/pkg/sub/path", "@scope/pkg")]
    [InlineData("openai/helpers", "openai")]
    [InlineData("./local", null)]
    [InlineData("node:fs", null)]
    public void Npm_specifier_resolves_to_package(string specifier, string? expected) =>
        Assert.Equal(expected, AiEstateScanner.NpmPackageOf(specifier));
}
