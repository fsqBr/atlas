using System.Text;
using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.JavaScript;

namespace Atlas.Scanner.Tests;

public class JavaScriptScannerTests
{
    private sealed class Reader(Dictionary<string, string> files) : IArtifactReader
    {
        public string RootPath => "/mem";

        public IEnumerable<string> EnumerateFiles(string searchPattern)
        {
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

    private static async Task<List<FindingCandidate>> RunAsync(Dictionary<string, string> files)
    {
        var sink = new Sink();
        var result = await new JavaScriptScanner().ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "repo", Workspace = new Reader(files),
            Languages = new Dictionary<string, LanguageAnalysisResult>(), Findings = sink, Today = new DateOnly(2026, 8, 29),
        }, CancellationToken.None);
        Assert.True(result.Succeeded);
        return sink.Items;
    }

    [Fact]
    public async Task Inventories_frameworks_flags_legacy_ones_and_unsafe_patterns()
    {
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["src/Web/package.json"] = """{"dependencies":{"react":"^18.2.0","jquery":"^1.12.4","knockout":"3.5.1"},"devDependencies":{"typescript":"^5","gulp":"^4"}}""",
            ["src/Web/Views/Shared/_Layout.cshtml"] = """<script src="~/Scripts/angular.min.js"></script><script src="~/Scripts/jquery-1.10.2.min.js"></script>""",
            ["src/Web/Scripts/app.js"] = """
                var html = "<b>" + user.name + "</b>";
                el.innerHTML = html + "!";
                eval(code);
                $.ajax({ url: "http://api.internal/orders" });
                fetch("https://ok.example/x");
                """,
            ["src/Web/Scripts/lib/jquery-1.10.2.min.js"] = "eval(x)",
        });

        var inventory = Assert.Single(findings, f => f.RuleId == JavaScriptScanner.RuleIds.Inventory);
        Assert.Contains("React", inventory.Message);
        Assert.Contains("AngularJS 1.x (legacy)", inventory.Message);
        Assert.Contains("Knockout (legacy)", inventory.Message);
        Assert.Contains("jQuery 1.x/2.x (legacy)", inventory.Message);
        Assert.Contains("gulp (legacy)", inventory.Message);
        Assert.Equal("1", inventory.Data!["files"]); // vendored jquery is skipped

        var legacy = findings.Where(f => f.RuleId == JavaScriptScanner.RuleIds.LegacyFramework).Select(f => f.Data!["name"]).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(["AngularJS 1.x", "Knockout", "gulp", "jQuery 1.x/2.x"], legacy);

        Assert.Single(findings, f => f.RuleId == JavaScriptScanner.RuleIds.Eval && f.Evidence.FilePath == "src/Web/Scripts/app.js");
        var dom = Assert.Single(findings, f => f.RuleId == JavaScriptScanner.RuleIds.DomInjection);
        Assert.Equal(2, dom.Evidence.LineStart);
        Assert.Equal(Severity.High, dom.Severity);
        var url = Assert.Single(findings, f => f.RuleId == JavaScriptScanner.RuleIds.InsecureUrl);
        Assert.Contains("http://api.internal", url.Message);
    }

    [Fact]
    public async Task Stays_silent_without_any_front_end()
    {
        var findings = await RunAsync(new Dictionary<string, string> { ["src/App/Program.cs"] = "class P {}" });
        Assert.Empty(findings);
    }

    [Fact]
    public void Rules_are_bilingual()
    {
        var scanner = new JavaScriptScanner();
        Assert.Equal(5, scanner.Rules.Count);
        Assert.All(scanner.Rules, r => Assert.True(r.Localizations!.ContainsKey("pt-BR")));
        Assert.Equal(FindingCategory.Modernization, scanner.Rules.Single(r => r.Id == JavaScriptScanner.RuleIds.LegacyFramework).Category);
    }

    [Fact]
    public void Reads_dependencies_and_marks_jquery_majors()
    {
        var deps = JavaScriptScanner.ReadDependencies("""{"dependencies":{"jquery":"~3.7.1","vue":"3.4.0","angular":"1.8.3","left-pad":"1"}}""").ToList();
        Assert.Contains(deps, d => d.Name == "jQuery" && !d.Legacy);
        Assert.Contains(deps, d => d.Name == "Vue" && !d.Legacy);
        Assert.Contains(deps, d => d.Name == "AngularJS 1.x" && d.Legacy);
        Assert.DoesNotContain(deps, d => d.Package == "left-pad");
        Assert.Empty(JavaScriptScanner.ReadDependencies("{not json"));
    }
}
