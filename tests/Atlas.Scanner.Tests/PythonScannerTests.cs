using System.Text;
using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Dependencies.Vulnerabilities;
using Atlas.Scanner.Python;

namespace Atlas.Scanner.Tests;

public class PythonScannerTests
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

    private sealed class StubVulnerabilitySource : IVulnerabilitySource
    {
        public string? BundleVersion => "stub";

        public Task<IReadOnlyList<VulnerabilityMatch>> FindAsync(string packageId, string version, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VulnerabilityMatch>>([]);

        public Task<IReadOnlyList<VulnerabilityMatch>> FindAsync(string ecosystem, string packageId, string version, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VulnerabilityMatch>>(
                ecosystem == "PyPI" && packageId == "django" && version == "1.11.29"
                    ? [new VulnerabilityMatch("GHSA-test-django", "SQL injection in Django 1.11 before 1.11.29+fix.", "HIGH", "1.11.30", ["CVE-2020-0000"])]
                    : []);
    }

    private sealed class Sink : IFindingSink
    {
        public List<FindingCandidate> Items { get; } = [];

        public void Emit(FindingCandidate candidate) => Items.Add(candidate);
    }

    private static async Task<List<FindingCandidate>> RunAsync(Dictionary<string, string> files)
    {
        var sink = new Sink();
        var result = await new PythonScanner(new StubVulnerabilitySource()).ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "repo", Workspace = new Reader(files),
            Languages = new Dictionary<string, LanguageAnalysisResult>(), Findings = sink, Today = new DateOnly(2026, 8, 31),
        }, CancellationToken.None);
        Assert.True(result.Succeeded);
        return sink.Items;
    }

    [Fact]
    public async Task Requirements_pins_yield_inventory_legacy_frameworks_and_osv_match()
    {
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["portal/requirements.txt"] = """
                # pinned legacy stack
                Django==1.11.29
                flask == 1.1.4
                nose
                pycrypto==2.6.1
                requests>=2.31
                -r requirements-dev.txt
                """,
        });

        var inventory = Assert.Single(findings, f => f.RuleId == PythonScanner.RuleIds.Inventory);
        Assert.Equal("5", inventory.Data!["packages"]); // -r line is an option, not a package

        var legacy = findings.Where(f => f.RuleId == PythonScanner.RuleIds.LegacyFramework)
            .ToDictionary(f => f.Data!["name"], f => f.Severity);
        Assert.Equal(Severity.High, legacy["Django 1.x"]); // first matching row wins — not also "Django 2.x"
        Assert.Equal(Severity.Medium, legacy["Flask 0.x/1.x"]);
        Assert.Equal(Severity.Medium, legacy["nose"]); // unpinned + no version gate still flags
        Assert.Equal(Severity.High, legacy["PyCrypto"]);
        Assert.Equal(4, legacy.Count);

        var vuln = Assert.Single(findings, f => f.RuleId == PythonScanner.RuleIds.VulnerablePackage);
        Assert.Contains("GHSA-test-django", vuln.Title);
        Assert.Equal(Severity.High, vuln.Severity);
        Assert.Contains("1.11.30", vuln.Remediation!);
    }

    [Fact]
    public async Task Pyproject_floor_below_support_is_flagged_and_ranges_are_not_judged()
    {
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["svc/pyproject.toml"] = """
                [project]
                name = "svc"
                description = "A nose for reporting; ships with pycrypto-free crypto"
                license = "MIT"
                requires-python = ">=3.8"
                dependencies = [
                  "celery==4.4.7",
                  "tornado==5.1.1",
                  "httpx>=0.27",
                ]

                [project.urls]
                Homepage = "https://acmedemo.example"
                """,
        });

        var eol = Assert.Single(findings, f => f.RuleId == PythonScanner.RuleIds.InterpreterEol);
        Assert.Equal(Severity.Medium, eol.Severity);
        Assert.Equal("3.8", eol.Data!["target"]);

        // Quoted strings OUTSIDE dependency arrays (name, description, urls) are never packages:
        // no phantom "nose"/"pycrypto" findings, and the inventory counts exactly the array.
        var inventory = Assert.Single(findings, f => f.RuleId == PythonScanner.RuleIds.Inventory);
        Assert.Equal("3", inventory.Data!["packages"]);
        Assert.DoesNotContain(findings, f => f.RuleId == PythonScanner.RuleIds.LegacyFramework && f.Data!["name"] is "nose" or "PyCrypto");

        var legacy = findings.Where(f => f.RuleId == PythonScanner.RuleIds.LegacyFramework).Select(f => f.Data!["name"]).ToList();
        Assert.Contains("Celery 4.x or older", legacy);
        Assert.Contains("Tornado 5.x or older", legacy);
        Assert.DoesNotContain(findings, f => f.RuleId == PythonScanner.RuleIds.VulnerablePackage); // no pinned match
    }

    [Fact]
    public async Task Python2_support_is_high_severity_and_pipfile_parses()
    {
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["legacy/setup.py"] = """
                from setuptools import setup
                setup(name="legacy", python_requires="==2.7.*", install_requires=["Django==1.11.29"])
                """,
            ["worker/Pipfile"] = """
                [requires]
                python_version = "3.6"

                [packages]
                django = "==1.11.29"
                nose = "*"
                """,
        });

        var eols = findings.Where(f => f.RuleId == PythonScanner.RuleIds.InterpreterEol).ToList();
        Assert.Contains(eols, f => f.Data!["target"] == "2" && f.Severity == Severity.High);
        Assert.Contains(eols, f => f.Data!["target"] == "3.6" && f.Severity == Severity.Medium);

        Assert.Single(findings, f => f.RuleId == PythonScanner.RuleIds.LegacyFramework && f.Data!["name"] == "Django 1.x");
        Assert.Single(findings, f => f.RuleId == PythonScanner.RuleIds.LegacyFramework && f.Data!["name"] == "nose"); // "*" in a Pipfile still counts
        // One finding per (package, version) across the estate — same convention as the .NET dependency scanner.
        Assert.Single(findings, f => f.RuleId == PythonScanner.RuleIds.VulnerablePackage);
    }

    [Fact]
    public async Task Poetry_pyproject_yields_interpreter_floor_and_framework_gates()
    {
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["poetry/pyproject.toml"] = """
                [tool.poetry]
                name = "poetry-app"
                description = "mentions nose and pycrypto and django in prose"

                [tool.poetry.dependencies]
                python = "^3.8"
                django = "^1.11"
                requests = { version = ">=2.0", extras = ["socks"] }

                [tool.poetry.group.dev.dependencies]
                nose = "*"
                """,
        });

        var eol = Assert.Single(findings, f => f.RuleId == PythonScanner.RuleIds.InterpreterEol);
        Assert.Equal("3.8", eol.Data!["target"]); // Poetry's python key is the interpreter constraint

        var legacy = findings.Where(f => f.RuleId == PythonScanner.RuleIds.LegacyFramework)
            .ToDictionary(f => f.Data!["name"], f => f.Severity);
        Assert.Equal(Severity.High, legacy["Django 1.x"]); // the ^1.11 floor gates the major
        Assert.Equal(Severity.Medium, legacy["nose"]);
        Assert.Equal(2, legacy.Count); // prose words never become packages
        Assert.DoesNotContain(findings, f => f.RuleId == PythonScanner.RuleIds.VulnerablePackage); // floors are not pins
    }

    [Fact]
    public async Task No_python_content_means_no_findings()
    {
        var findings = await RunAsync(new Dictionary<string, string> { ["src/app.cs"] = "class X { }" });

        Assert.Empty(findings);
    }
}
