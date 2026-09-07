using System.Text.Json;
using Atlas.Application.Assessments;
using Atlas.Scanner.Abstractions;
using Atlas.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.IntegrationTests;

/// <summary>
/// Rule regression gate: the scanners run over a fixed corpus and the number of
/// candidates per rule must match the committed snapshot. A rule change that
/// silently doubles (or drops) findings fails here. Update the snapshot on
/// purpose with ATLAS_UPDATE_CORPUS=1.
/// </summary>
public sealed class CorpusRegressionTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string CorpusRoot => Path.Combine(AppContext.BaseDirectory, "Corpus", "legacy-shop");

    private static string SnapshotPath => Path.Combine(FindRepoTestsDirectory(), "Corpus", "legacy-shop.snapshot.json");

    private static string FingerprintSnapshotPath => Path.Combine(FindRepoTestsDirectory(), "Corpus", "legacy-shop.fingerprints.json");

    /// <summary>What the fingerprint gate pins: every shipped rule's version and the fingerprints it produced on the corpus.</summary>
    private sealed record FingerprintSnapshot(SortedDictionary<string, string> RuleVersions, SortedDictionary<string, List<string>> Fingerprints);

    [Fact]
    public async Task Legacy_shop_corpus_matches_the_snapshot()
    {
        Assert.True(Directory.Exists(CorpusRoot), $"corpus not copied to output: {CorpusRoot}");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Atlas:Secrets:HmacKeyBase64"] = Convert.ToBase64String(new byte[32]),
                // No registry lookups in the gate: every package classifies as "unknown", deterministically and offline.
                ["Atlas:Licenses:Enabled"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAtlasScanning(configuration);
        services.AddSingleton<InProcessScanExecutor>();
        await using var provider = services.BuildServiceProvider();

        var scannerIds = provider.GetServices<IScanner>().Select(s => s.Descriptor.Id).ToList();
        var request = new WorkspaceScanRequest(Guid.NewGuid(), "legacy-shop", CorpusRoot,
            scannerIds.ToDictionary(id => id, _ => Guid.NewGuid(), StringComparer.Ordinal), new DateOnly(2026, 8, 29));

        var outcome = await provider.GetRequiredService<InProcessScanExecutor>().ExecuteAsync(request, CancellationToken.None);
        Assert.All(outcome.Scanners, s => Assert.True(s.Succeeded, $"{s.ScannerId}: {s.Error}"));

        var actual = outcome.Scanners.SelectMany(s => s.Candidates)
            .GroupBy(c => c.RuleId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // Exclusions must hold: nothing from the vendored folder or the designer file.
        Assert.DoesNotContain(outcome.Scanners.SelectMany(s => s.Candidates), c => c.Evidence.FilePath?.Contains("vendor", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(outcome.Scanners.SelectMany(s => s.Candidates), c => c.Evidence.FilePath?.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) == true);

        // The corpus is built to trigger these families.
        foreach (var expected in new[]
        {
            "dependency.framework.end-of-life", "dependency.migration-blocker.mb-001", "dependency.migration-blocker.mb-003",
            "sec.sql.string-concatenation", "sec.crypto.weak-hash", "sec.config.debug-enabled", "secrets.connection-string-password",
            "privacy.pii.identifier", "privacy.pii.contact", "privacy.pii.financial", "privacy.leak.log", "privacy.leak.exception",
            "quality.duplication.block", "quality.api.legacy", "quality.complexity.method", "quality.tests.none",
            "database.inventory", "database.pii-column", "database.trigger",
        })
        {
            Assert.True(actual.ContainsKey(expected), $"expected at least one '{expected}' candidate; got: {string.Join(", ", actual.Keys)}");
        }

        if (Environment.GetEnvironmentVariable("ATLAS_UPDATE_CORPUS") == "1" || !File.Exists(SnapshotPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            await File.WriteAllTextAsync(SnapshotPath, JsonSerializer.Serialize(actual, Json) + Environment.NewLine);
            return;
        }

        var snapshot = JsonSerializer.Deserialize<Dictionary<string, int>>(await File.ReadAllTextAsync(SnapshotPath)) ?? [];
        var differences = snapshot.Keys.Union(actual.Keys, StringComparer.Ordinal)
            .Where(k => snapshot.GetValueOrDefault(k) != actual.GetValueOrDefault(k))
            .Select(k => $"{k}: snapshot {snapshot.GetValueOrDefault(k)} → now {actual.GetValueOrDefault(k)}")
            .ToList();
        Assert.True(differences.Count == 0,
            "Rule output drifted from the corpus snapshot (run with ATLAS_UPDATE_CORPUS=1 if intended):\n" + string.Join("\n", differences));

        await AssertFingerprintsPinned(provider, outcome);
    }

    /// <summary>
    /// Fingerprint gate (ADR-012). A finding's identity is (rule id, rule MAJOR, repository, file, symbol-or-snippet).
    /// If a rule starts producing different fingerprints for the same corpus — its evidence location moved, its symbol
    /// changed, it was renamed — every customer's open findings for that rule close and reopen as new ones and their
    /// waivers stop applying. That is exactly what a MAJOR bump is for, so this test fails when fingerprints move while
    /// the rule's major version does not. Intended changes: bump the rule's MAJOR (the waiver-migration path takes over),
    /// then refresh the snapshot with ATLAS_UPDATE_CORPUS=1.
    /// </summary>
    private static async Task AssertFingerprintsPinned(ServiceProvider provider, WorkspaceScanOutcome outcome)
    {
        var rules = provider.GetServices<IScanner>().SelectMany(s => s.Rules).ToDictionary(r => r.Id, r => r.Version, StringComparer.Ordinal);
        var actual = new FingerprintSnapshot(new SortedDictionary<string, string>(rules, StringComparer.Ordinal), new SortedDictionary<string, List<string>>(StringComparer.Ordinal));
        foreach (var c in outcome.Scanners.SelectMany(s => s.Candidates))
        {
            var fp = Atlas.Domain.Findings.FindingFingerprint.Compute(c.RuleId, Atlas.Domain.Findings.FindingFingerprint.MajorVersionOf(rules[c.RuleId]), "legacy-shop", c.Evidence.FilePath, c.Evidence.Symbol ?? c.Evidence.SnippetHash);
            if (!actual.Fingerprints.TryGetValue(c.RuleId, out var list))
            {
                actual.Fingerprints[c.RuleId] = list = [];
            }

            if (!list.Contains(fp))
            {
                list.Add(fp);
            }
        }

        foreach (var list in actual.Fingerprints.Values)
        {
            list.Sort(StringComparer.Ordinal);
        }

        if (Environment.GetEnvironmentVariable("ATLAS_UPDATE_CORPUS") == "1" || !File.Exists(FingerprintSnapshotPath))
        {
            await File.WriteAllTextAsync(FingerprintSnapshotPath, JsonSerializer.Serialize(actual, Json) + Environment.NewLine);
            return;
        }

        var pinned = JsonSerializer.Deserialize<FingerprintSnapshot>(await File.ReadAllTextAsync(FingerprintSnapshotPath))!;
        var problems = new List<string>();
        foreach (var (ruleId, fingerprints) in pinned.Fingerprints)
        {
            var now = actual.Fingerprints.GetValueOrDefault(ruleId) ?? [];
            if (fingerprints.SequenceEqual(now, StringComparer.Ordinal))
            {
                continue;
            }

            var pinnedMajor = Atlas.Domain.Findings.FindingFingerprint.MajorVersionOf(pinned.RuleVersions.GetValueOrDefault(ruleId) ?? "1.0.0");
            var currentMajor = rules.TryGetValue(ruleId, out var v) ? Atlas.Domain.Findings.FindingFingerprint.MajorVersionOf(v) : -1;
            if (currentMajor > pinnedMajor)
            {
                continue; // MAJOR bumped on purpose: new fingerprints are expected (refresh the snapshot)
            }

            problems.Add($"{ruleId} (version {rules.GetValueOrDefault(ruleId) ?? "removed"}): {fingerprints.Except(now).Count()} pinned fingerprint(s) gone, {now.Except(fingerprints).Count()} new");
        }

        foreach (var (ruleId, version) in pinned.RuleVersions)
        {
            if (rules.TryGetValue(ruleId, out var v) && Atlas.Domain.Findings.FindingFingerprint.MajorVersionOf(v) < Atlas.Domain.Findings.FindingFingerprint.MajorVersionOf(version))
            {
                problems.Add($"{ruleId}: major version went backwards ({version} → {v})");
            }
        }

        Assert.True(problems.Count == 0,
            "Finding fingerprints moved without a rule MAJOR bump (ADR-012). Bump the rule's major version — the waiver migration "
            + "path then applies — and refresh with ATLAS_UPDATE_CORPUS=1:\n" + string.Join("\n", problems));
    }

    private static string FindRepoTestsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Atlas.IntegrationTests.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
