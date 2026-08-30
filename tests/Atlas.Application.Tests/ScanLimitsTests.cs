using Atlas.Application.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Application.Tests;

public class ScanLimitsTests
{
    private sealed class FakeReader(int files) : IArtifactReader
    {
        public string RootPath => "/ws";

        public IEnumerable<string> EnumerateFiles(string searchPattern) => Enumerable.Range(0, files).Select(i => $"f{i}.cs");

        public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);

        public Stream OpenRead(string relativePath) => Stream.Null;
    }

    private sealed class FakeFactory(int files) : IArtifactReaderFactory
    {
        public IArtifactReader Create(string rootPath) => new FakeReader(files);
    }

    private sealed class SlowScanner(TimeSpan delay) : IScanner
    {
        public ScannerDescriptor Descriptor { get; } = new("test.slow", "Slow", "1.0", FindingCategory.Quality, []);

        public IReadOnlyList<RuleSpec> Rules { get; } = [];

        public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            context.Findings.Emit(new FindingCandidate("test.slow.rule", Severity.Low, ConfidenceLevel.High, "t", "m", new EvidenceCandidate("a.cs")));
            return ScanResult.Success();
        }
    }

    private static WorkspaceScanRequest Request(params string[] scanners) =>
        new(Guid.NewGuid(), "r", "/ws", scanners.ToDictionary(s => s, _ => Guid.NewGuid(), StringComparer.Ordinal), new DateOnly(2026, 8, 29));

    [Fact]
    public async Task A_scanner_over_its_timeout_fails_alone_and_the_others_still_run()
    {
        var executor = new InProcessScanExecutor(new FakeFactory(10), [], [new SlowScanner(TimeSpan.FromSeconds(5)), new FastScanner()],
            NullLogger<InProcessScanExecutor>.Instance, new ScanLimits { ScannerTimeoutMinutes = 1 } with { ScannerTimeoutMinutes = 1 }, TimeSpan.FromMilliseconds(200));

        var outcome = await executor.ExecuteAsync(Request("test.slow", "test.fast"), CancellationToken.None);

        var slow = outcome.Scanners.Single(s => s.ScannerId == "test.slow");
        Assert.False(slow.Succeeded);
        Assert.Contains("timed out", slow.Error);
        Assert.True(outcome.Scanners.Single(s => s.ScannerId == "test.fast").Succeeded);
    }

    [Fact]
    public async Task Oversized_workspace_is_refused_with_an_actionable_message()
    {
        var executor = new InProcessScanExecutor(new FakeFactory(1_000), [], [new FastScanner()],
            NullLogger<InProcessScanExecutor>.Instance, new ScanLimits { MaxFiles = 100 });

        var ex = await Assert.ThrowsAsync<WorkspaceTooLargeException>(() => executor.ExecuteAsync(Request("test.fast"), CancellationToken.None));
        Assert.Equal((1_000, 100), (ex.Files, ex.Limit));
        Assert.Contains(".atlasignore", ex.Message);
    }

    private sealed class FastScanner : IScanner
    {
        public ScannerDescriptor Descriptor { get; } = new("test.fast", "Fast", "1.0", FindingCategory.Quality, []);

        public IReadOnlyList<RuleSpec> Rules { get; } = [];

        public Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken) => Task.FromResult(ScanResult.Success());
    }
}
