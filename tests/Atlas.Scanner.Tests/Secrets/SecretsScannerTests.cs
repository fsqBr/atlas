using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Runtime;
using Atlas.Scanner.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Scanner.Tests.Secrets;

public class SecretsScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-secrets").FullName;

    // Deliberately fake values shaped like real secrets.
    private const string FakeAwsKey = "AKIAABCDEFGHIJKLMNOP";
    private const string FakeDbPassword = "Sup3rS3cr3tV4lue!";
    private const string FakeApiKey = "q8Zt3vB9kL2mN7xP4wR6sT1u";
    private static readonly string Key = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());

    public SecretsScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "certs"));

        File.WriteAllText(Path.Combine(_root, "src", "appsettings.json"),
            $$"""{ "ConnectionStrings": { "Db": "Server=db;Database=app;User Id=sa;Password={{FakeDbPassword}};" } }""");
        File.WriteAllText(Path.Combine(_root, "src", "Config.cs"),
            $$"""
            class Config
            {
                const string Aws = "{{FakeAwsKey}}";
                const string ApiKey = "{{FakeApiKey}}";
                const string Weak = "aaaaaaaaaaaaaaaa";
            }
            """);
        File.WriteAllText(Path.Combine(_root, "src", "notes.txt"), "-----BEGIN RSA PRIVATE KEY-----\nMIIE...\n-----END RSA PRIVATE KEY-----");
        File.WriteAllText(Path.Combine(_root, ".env.example"), "API_KEY=\"your-api-key-here\"\nPASSWORD=\"changeme\"\n");
        File.WriteAllText(Path.Combine(_root, "bin", "leak.txt"), $"aws={FakeAwsKey}");
        File.WriteAllText(Path.Combine(_root, "certs", "server.pfx"), "binary\0stuff");
    }

    private async Task<IReadOnlyList<FindingCandidate>> ScanAsync(string keyBase64)
    {
        var scanner = new SecretsScanner(new SecretsScannerOptions { HmacKeyBase64 = keyBase64 }, NullLogger<SecretsScanner>.Instance);
        var sink = new InMemoryFindingSink();
        var result = await scanner.ExecuteAsync(new ScanContext
        {
            AssessmentId = Guid.NewGuid(),
            ScanId = Guid.NewGuid(),
            RepositoryKey = "repo",
            Workspace = new ContainedArtifactReader(_root),
            Languages = new Dictionary<string, LanguageAnalysisResult>(),
            Findings = sink,
            Today = new DateOnly(2026, 8, 28),
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        return sink.Candidates;
    }

    [Fact]
    public async Task Detects_known_secret_shapes_and_key_files()
    {
        var candidates = await ScanAsync(Key);
        var rules = candidates.Select(c => c.RuleId).ToHashSet();

        Assert.Contains("secrets.aws-access-key", rules);
        Assert.Contains("secrets.connection-string-password", rules);
        Assert.Contains("secrets.private-key", rules);
        Assert.Contains("secrets.generic-assignment", rules);
        Assert.Contains(SecretDetectors.KeyFileRuleId, rules);
    }

    [Fact]
    public async Task Skips_placeholders_low_entropy_and_build_output()
    {
        var candidates = await ScanAsync(Key);

        Assert.DoesNotContain(candidates, c => c.Evidence.FilePath!.EndsWith(".env.example"));
        Assert.DoesNotContain(candidates, c => c.Evidence.FilePath!.Contains("bin"));
        Assert.Single(candidates, c => c.RuleId == "secrets.generic-assignment"); // ApiKey yes, Weak no
    }

    [Fact]
    public async Task Never_stores_the_secret_value_anywhere()
    {
        var candidates = await ScanAsync(Key);

        foreach (var c in candidates)
        {
            var everything = string.Join('\n', c.Title, c.Message, c.Evidence.Symbol, c.Remediation,
                string.Join(',', c.Data?.Values ?? []));
            Assert.DoesNotContain(FakeAwsKey, everything);
            Assert.DoesNotContain(FakeDbPassword, everything);
            Assert.DoesNotContain(FakeApiKey, everything);
        }

        Assert.All(candidates.Where(c => c.RuleId != SecretDetectors.KeyFileRuleId),
            c => Assert.StartsWith("hmac:", c.Evidence.Symbol));
    }

    [Fact]
    public async Task Fingerprints_are_stable_per_key_and_differ_across_keys()
    {
        var first = await ScanAsync(Key);
        var again = await ScanAsync(Key);
        var otherKey = await ScanAsync(Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));

        string Aws(IReadOnlyList<FindingCandidate> c) => c.Single(x => x.RuleId == "secrets.aws-access-key").Evidence.Symbol!;

        Assert.Equal(Aws(first), Aws(again));
        Assert.NotEqual(Aws(first), Aws(otherKey));
    }

    [Fact]
    public void Every_detector_is_a_declared_rule()
    {
        var scanner = new SecretsScanner(new SecretsScannerOptions { HmacKeyBase64 = Key }, NullLogger<SecretsScanner>.Instance);
        var declared = scanner.Rules.Select(r => r.Id).ToHashSet();

        Assert.All(SecretDetectors.All, d => Assert.Contains(d.Id, declared));
        Assert.Contains(SecretDetectors.KeyFileRuleId, declared);
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
