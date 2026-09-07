using System.Text;
using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Licenses;

namespace Atlas.Scanner.Tests;

public class LicenseScannerTests
{
    [Theory]
    [InlineData("MIT", LicenseClass.Permissive)]
    [InlineData("Apache-2.0", LicenseClass.Permissive)]
    [InlineData("LGPL-2.1-or-later", LicenseClass.WeakCopyleft)]
    [InlineData("MPL-2.0", LicenseClass.WeakCopyleft)]
    [InlineData("GPL-3.0-only", LicenseClass.StrongCopyleft)]
    [InlineData("AGPL-3.0", LicenseClass.StrongCopyleft)]
    [InlineData("SSPL-1.0", LicenseClass.Restricted)]
    [InlineData("CC-BY-NC-4.0", LicenseClass.Restricted)]
    [InlineData("(MIT OR GPL-3.0-only)", LicenseClass.Permissive)]
    [InlineData("MIT AND GPL-2.0-only", LicenseClass.StrongCopyleft)]
    [InlineData("GPL-2.0 WITH Classpath-exception-2.0", LicenseClass.WeakCopyleft)]
    [InlineData("SEE LICENSE IN LICENSE.txt", LicenseClass.Restricted)]
    [InlineData("", LicenseClass.Unknown)]
    [InlineData("LicenseRef-File", LicenseClass.Unknown)]
    public void Classifies_spdx_expressions(string expression, LicenseClass expected) => Assert.Equal(expected, LicenseClassifier.Classify(expression));

    [Fact]
    public void Maps_legacy_license_urls()
    {
        Assert.Equal("MIT", LicenseClassifier.FromUrl("https://opensource.org/licenses/MIT"));
        Assert.Equal("Apache-2.0", LicenseClassifier.FromUrl("http://www.apache.org/licenses/LICENSE-2.0"));
        Assert.Equal("GPL-3.0", LicenseClassifier.FromUrl("https://www.gnu.org/licenses/gpl-3.0.html"));
        Assert.Null(LicenseClassifier.FromUrl("https://example.test/custom"));
        Assert.Equal("MS-NET-Library", LicenseClassifier.FromUrl("http://go.microsoft.com/fwlink/?LinkID=320539"));
        Assert.Equal(LicenseClass.Permissive, LicenseClassifier.Classify("MS-NET-Library"));
        Assert.Equal("MIT", LicenseClassifier.FromUrl("https://raw.github.com/JamesNK/Newtonsoft.Json/master/LICENSE.md"));
        var nugetUrl = RegistryLicenseResolver.ParseNuspec("X", "1.0", """<package><metadata><licenseUrl>https://licenses.nuget.org/Apache-2.0</licenseUrl></metadata></package>""");
        Assert.Equal("Apache-2.0", nugetUrl.Expression);
    }

    [Fact]
    public void Parses_nuspec_and_npm_metadata()
    {
        var expr = RegistryLicenseResolver.ParseNuspec("Newtonsoft.Json", "13.0.3", """<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Newtonsoft.Json</id><license type="expression">MIT</license></metadata></package>""");
        Assert.Equal("MIT", expr.Expression);
        Assert.Equal(LicenseClass.Permissive, expr.Class);

        var url = RegistryLicenseResolver.ParseNuspec("Old", "1.0", """<package><metadata><licenseUrl>https://www.gnu.org/licenses/lgpl-3.0.html</licenseUrl></metadata></package>""");
        Assert.Equal("LGPL-3.0", url.Expression);
        Assert.Equal(LicenseClass.WeakCopyleft, url.Class);

        var file = RegistryLicenseResolver.ParseNuspec("X", "1.0", """<package><metadata><license type="file">LICENSE.txt</license></metadata></package>""");
        Assert.Equal(LicenseClass.Unknown, file.Class);

        var npm = RegistryLicenseResolver.ParseNpm("left-pad", "1.3.0", """{"name":"left-pad","license":"WTFPL"}""");
        Assert.Equal(LicenseClass.Permissive, npm.Class);
        var npmObj = RegistryLicenseResolver.ParseNpm("x", "1", """{"license":{"type":"AGPL-3.0"}}""");
        Assert.Equal(LicenseClass.StrongCopyleft, npmObj.Class);
        var npmArr = RegistryLicenseResolver.ParseNpm("x", "1", """{"licenses":[{"type":"MIT"},{"type":"GPL-2.0"}]}""");
        Assert.Equal("MIT OR GPL-2.0", npmArr.Expression);
        Assert.Equal(LicenseClass.Permissive, npmArr.Class);
        Assert.Equal(LicenseClass.Unknown, RegistryLicenseResolver.ParseNpm("x", "1", "garbage").Class);
    }

    private sealed class Reader(Dictionary<string, string> files) : IArtifactReader
    {
        public string RootPath => "/mem";
        public IEnumerable<string> EnumerateFiles(string searchPattern) => files.Keys.Where(k => k.EndsWith(searchPattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase) || Path.GetFileName(k).Equals(searchPattern, StringComparison.OrdinalIgnoreCase));
        public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(files[relativePath]);
        public Stream OpenRead(string relativePath) => new MemoryStream(Encoding.UTF8.GetBytes(files[relativePath]));
    }

    private sealed class Sink : IFindingSink
    {
        public List<FindingCandidate> Items { get; } = [];
        public void Emit(FindingCandidate candidate) => Items.Add(candidate);
    }

    private sealed class FakeResolver(Dictionary<string, string?> byId) : ILicenseResolver
    {
        public Task<IReadOnlyList<PackageLicense>> ResolveAsync(IReadOnlyList<(string Ecosystem, string Id, string Version)> packages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PackageLicense>>(packages.Select(p =>
            {
                var expr = byId.GetValueOrDefault(p.Id);
                return new PackageLicense(p.Ecosystem, p.Id, p.Version, expr, null, LicenseClassifier.Classify(expr), "fake");
            }).ToList());
    }

    [Fact]
    public async Task Emits_inventory_with_components_and_policy_findings()
    {
        var project = new ProjectFact("src/App/App.csproj", "App", true, "net8.0",
            [new PackageReferenceFact("Newtonsoft.Json", "13.0.3", PackageReferenceOrigin.PackageReference), new PackageReferenceFact("SharpZipLib", "1.4.2", PackageReferenceOrigin.PackageReference), new PackageReferenceFact("Mystery", "1.0.0", PackageReferenceOrigin.PackageReference), new PackageReferenceFact("Ghostscript.NET", "1.2.3", PackageReferenceOrigin.PackagesConfig)],
            [], []);
        var language = new LanguageAnalysisResult("csharp", AnalysisTier.Syntactic, [], [project], [], new LanguageTotals(0, 0, 0, 0, 0, 0), null, [], [], [], []);
        var resolver = new FakeResolver(new Dictionary<string, string?> { ["Newtonsoft.Json"] = "MIT", ["SharpZipLib"] = "MIT", ["Ghostscript.NET"] = "AGPL-3.0", ["Mystery"] = null, ["left-pad"] = "WTFPL", ["copyleft-thing"] = "LGPL-3.0" });
        var scanner = new LicenseScanner(resolver, new LicenseOptions { Denied = ["AGPL-3.0"] });
        var sink = new Sink();

        var result = await scanner.ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "r",
            Workspace = new Reader(new Dictionary<string, string> { ["web/package-lock.json"] = """{"lockfileVersion":3,"packages":{"":{"name":"web"},"node_modules/left-pad":{"version":"1.3.0"},"node_modules/copyleft-thing":{"version":"2.0.0"},"node_modules/jest":{"version":"29.0.0","dev":true}}}""" }),
            Languages = new Dictionary<string, LanguageAnalysisResult> { ["csharp"] = language }, Findings = sink, Today = new DateOnly(2026, 8, 30),
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        var inventory = Assert.Single(sink.Items, f => f.RuleId == LicenseScanner.RuleIds.Inventory);
        Assert.Equal("6", inventory.Data!["total"]); // 4 nuget + 2 npm (dev dependency excluded)
        Assert.Contains("\"Id\":\"left-pad\"", inventory.Data["components"]);
        Assert.Equal("1", inventory.Data["unknown"]);

        var denied = Assert.Single(sink.Items, f => f.RuleId == LicenseScanner.RuleIds.Denied);
        Assert.Equal(Severity.Critical, denied.Severity);
        Assert.Contains("Ghostscript.NET", denied.Title);
        Assert.DoesNotContain(sink.Items, f => f.RuleId == LicenseScanner.RuleIds.StrongCopyleft); // denied wins over the class finding
        Assert.Single(sink.Items, f => f.RuleId == LicenseScanner.RuleIds.WeakCopyleft && f.Title.StartsWith("copyleft-thing"));
        var unknown = Assert.Single(sink.Items, f => f.RuleId == LicenseScanner.RuleIds.Unknown);
        Assert.Contains("Mystery@1.0.0", unknown.Message);
    }

    [Fact]
    public async Task Stays_silent_without_dependencies_and_rules_are_bilingual()
    {
        var scanner = new LicenseScanner(new FakeResolver([]), new LicenseOptions());
        var sink = new Sink();
        await scanner.ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "r", Workspace = new Reader([]),
            Languages = new Dictionary<string, LanguageAnalysisResult>(), Findings = sink, Today = new DateOnly(2026, 8, 30),
        }, CancellationToken.None);
        Assert.Empty(sink.Items);
        Assert.Equal(6, scanner.Rules.Count);
        Assert.All(scanner.Rules, r => Assert.True(r.Localizations!.ContainsKey("pt-BR")));
    }

    [Fact]
    public async Task Disabled_resolver_reports_not_looked_up_and_uses_the_cache_when_present()
    {
        var dir = Directory.CreateTempSubdirectory("atlas-lic");
        try
        {
            var cache = Path.Combine(dir.FullName, "licenses.json");
            File.WriteAllText(cache, """{"nuget:newtonsoft.json@13.0.3":{"Ecosystem":"nuget","Id":"Newtonsoft.Json","Version":"13.0.3","Expression":"MIT","Url":null,"Class":1,"Source":"registry"}}""");
            var resolver = new RegistryLicenseResolver(new NoHttp(), new LicenseOptions { Enabled = false, CachePath = cache });

            var resolved = await resolver.ResolveAsync([("nuget", "Newtonsoft.Json", "13.0.3"), ("nuget", "Other", "1.0")], CancellationToken.None);

            Assert.Equal(LicenseClass.Permissive, resolved.Single(r => r.Id == "Newtonsoft.Json").Class);
            Assert.Equal("cache", resolved.Single(r => r.Id == "Newtonsoft.Json").Source);
            Assert.Equal("disabled", resolved.Single(r => r.Id == "Other").Source);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in this test");
    }
}
