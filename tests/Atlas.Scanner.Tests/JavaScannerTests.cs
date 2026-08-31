using System.Text;
using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Dependencies.Vulnerabilities;
using Atlas.Scanner.Java;

namespace Atlas.Scanner.Tests;

public class JavaScannerTests
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
                ecosystem == "Maven" && packageId == "log4j:log4j" && version == "1.2.17"
                    ? [new VulnerabilityMatch("GHSA-test-log4j1", "Deserialization of untrusted data in Log4j 1.x.", "HIGH", "2.0.0", ["CVE-2019-17571"])]
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
        var result = await new JavaScanner(new StubVulnerabilitySource()).ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(), ScanId = Guid.NewGuid(), RepositoryKey = "repo", Workspace = new Reader(files),
            Languages = new Dictionary<string, LanguageAnalysisResult>(), Findings = sink, Today = new DateOnly(2026, 8, 31),
        }, CancellationToken.None);
        Assert.True(result.Succeeded);
        return sink.Items;
    }

    private const string LegacyPom = """
        <project xmlns="http://maven.apache.org/POM/4.0.0">
          <modelVersion>4.0.0</modelVersion>
          <artifactId>legacy-portal</artifactId>
          <properties>
            <java.version>1.8</java.version>
            <log4j.version>1.2.17</log4j.version>
          </properties>
          <parent>
            <groupId>org.springframework.boot</groupId>
            <artifactId>spring-boot-starter-parent</artifactId>
            <version>1.5.22.RELEASE</version>
          </parent>
          <dependencies>
            <dependency><groupId>log4j</groupId><artifactId>log4j</artifactId><version>${log4j.version}</version></dependency>
            <dependency><groupId>org.springframework</groupId><artifactId>spring-core</artifactId><version>4.3.30.RELEASE</version></dependency>
            <dependency><groupId>javax.servlet</groupId><artifactId>javax.servlet-api</artifactId><version>3.1.0</version></dependency>
          </dependencies>
        </project>
        """;

    [Fact]
    public async Task Maven_module_yields_inventory_jdk_eol_legacy_frameworks_javax_and_osv_match()
    {
        var findings = await RunAsync(new Dictionary<string, string> { ["portal/pom.xml"] = LegacyPom });

        var inventory = Assert.Single(findings, f => f.RuleId == JavaScanner.RuleIds.Inventory);
        Assert.Equal("1", inventory.Data!["modules"]);
        Assert.Contains("8", inventory.Data["jdks"]);

        var jdk = Assert.Single(findings, f => f.RuleId == JavaScanner.RuleIds.JdkEol);
        Assert.Equal(Severity.High, jdk.Severity); // property ${java.version} 1.8 resolved to 8
        Assert.Equal("legacy-portal", jdk.Data!["module"]);

        var legacy = findings.Where(f => f.RuleId == JavaScanner.RuleIds.LegacyFramework).Select(f => f.Data!["name"]).ToList();
        Assert.Contains("Log4j 1.x", legacy); // version came from a resolved property
        Assert.Contains("Spring Framework 4.x or older", legacy);
        Assert.Contains("Spring Boot 1.x", legacy); // via the parent POM

        var javax = Assert.Single(findings, f => f.RuleId == JavaScanner.RuleIds.JavaxNamespace);
        Assert.Contains("javax.servlet:javax.servlet-api", javax.Data!["sample"]);

        var vuln = Assert.Single(findings, f => f.RuleId == JavaScanner.RuleIds.VulnerablePackage);
        Assert.Equal(Severity.High, vuln.Severity);
        Assert.Contains("GHSA-test-log4j1", vuln.Title);
        Assert.Contains("2.0.0", vuln.Remediation!);
    }

    [Fact]
    public async Task Gradle_module_reads_jdk_and_flags_only_truly_legacy_dependencies()
    {
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["svc/build.gradle"] = """
                plugins { id 'java' }
                sourceCompatibility = 11
                // TODO drop log4j:log4j:1.2.17 someday — comments are not dependencies
                dependencies {
                    implementation "commons-httpclient:commons-httpclient:3.1"
                    implementation "org.springframework:spring-core:5.3.39"
                }
                """,
        });

        var jdk = Assert.Single(findings, f => f.RuleId == JavaScanner.RuleIds.JdkEol);
        Assert.Equal(Severity.Medium, jdk.Severity); // 11: aging LTS, not the hard EOL of 8
        Assert.Equal("svc", jdk.Data!["module"]);

        var legacy = findings.Where(f => f.RuleId == JavaScanner.RuleIds.LegacyFramework).Select(f => f.Data!["name"]).ToList();
        Assert.Contains("Commons HttpClient 3", legacy);
        Assert.DoesNotContain(legacy, n => n.Contains("Spring")); // 5.x is above the below-major threshold
        Assert.DoesNotContain(legacy, n => n.Contains("Log4j")); // came from a comment: must not flag
        Assert.DoesNotContain(findings, f => f.RuleId == JavaScanner.RuleIds.VulnerablePackage);
        Assert.DoesNotContain(findings, f => f.RuleId == JavaScanner.RuleIds.JavaxNamespace);
    }

    [Fact]
    public async Task Gradle_java_version_enum_and_boot_starter_flag_correctly()
    {
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["legacy/build.gradle"] = """
                sourceCompatibility = JavaVersion.VERSION_1_8
                dependencies {
                    implementation 'org.springframework.boot:spring-boot-starter-web:1.5.22.RELEASE'
                }
                """,
        });

        var jdk = Assert.Single(findings, f => f.RuleId == JavaScanner.RuleIds.JdkEol);
        Assert.Equal("8", jdk.Data!["jdk"]); // VERSION_1_8 → 8, never "JDK 1"
        Assert.Equal(Severity.High, jdk.Severity);
        Assert.Contains(findings, f => f.RuleId == JavaScanner.RuleIds.LegacyFramework && f.Data!["name"] == "Spring Boot 1.x");
    }

    [Fact]
    public async Task Poms_under_build_output_are_ignored()
    {
        var findings = await RunAsync(new Dictionary<string, string>
        {
            ["portal/pom.xml"] = LegacyPom,
            ["portal/target/classes/META-INF/maven/com.acmedemo/legacy-portal/pom.xml"] = LegacyPom,
        });

        var inventory = Assert.Single(findings, f => f.RuleId == JavaScanner.RuleIds.Inventory);
        Assert.Equal("1", inventory.Data!["modules"]); // the target/ copy must not double-count
        Assert.Single(findings, f => f.RuleId == JavaScanner.RuleIds.JdkEol);
    }

    [Fact]
    public async Task No_java_content_means_no_findings()
    {
        var findings = await RunAsync(new Dictionary<string, string> { ["src/app.cs"] = "class X { }" });

        Assert.Empty(findings);
    }
}
