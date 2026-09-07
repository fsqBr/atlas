using System.Text.Json;
using System.Text.Json.Serialization;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Assessments;

/// <summary>Everything needed to analyze one materialized workspace: no database, no secrets beyond scanner config.</summary>
public sealed record WorkspaceScanRequest(
    Guid AssessmentId,
    string RepositoryKey,
    string WorkspaceRootPath,
    IReadOnlyDictionary<string, Guid> ScanIdsByScanner,
    DateOnly Today,
    IReadOnlyList<FileChangeFact>? History = null,
    IReadOnlyList<string>? ExcludeGlobs = null);

public sealed record ScannerOutcome(string ScannerId, bool Succeeded, string? Error, IReadOnlyList<FindingCandidate> Candidates)
{
    public static ScannerOutcome Failed(string scannerId, string error) => new(scannerId, false, error, []);
}

/// <summary>Language facts plus each scanner's candidates; the runner turns this into persisted scans/findings.</summary>
public sealed record WorkspaceScanOutcome(
    IReadOnlyDictionary<string, LanguageAnalysisResult> Languages,
    IReadOnlyList<ScannerOutcome> Scanners);

/// <summary>
/// Runs language analysis and scanners over a workspace. The in-process
/// implementation is the reference; the worker can swap in a child-process
/// executor so hostile input can at worst crash or exhaust a
/// disposable process, never the worker that owns the job lease.
/// </summary>
public interface IScanExecutor
{
    Task<WorkspaceScanOutcome> ExecuteAsync(WorkspaceScanRequest request, CancellationToken cancellationToken);
}

public sealed class InProcessScanExecutor(
    IArtifactReaderFactory readers,
    IEnumerable<ILanguageAnalyzer> analyzers,
    IEnumerable<IScanner> scanners,
    ILogger<InProcessScanExecutor> logger,
    ScanLimits? limits = null,
    TimeSpan? scannerTimeoutOverride = null) : IScanExecutor
{
    private readonly ScanLimits _limits = limits ?? new ScanLimits();

    public async Task<WorkspaceScanOutcome> ExecuteAsync(WorkspaceScanRequest request, CancellationToken cancellationToken)
    {
        var reader = readers.Create(request.WorkspaceRootPath, request.ExcludeGlobs ?? []);

        if (_limits.MaxFiles > 0)
        {
            var files = reader.EnumerateFiles("*").Count();
            if (files > _limits.MaxFiles)
            {
                throw new WorkspaceTooLargeException(files, _limits.MaxFiles);
            }
        }

        var scannerTimeout = scannerTimeoutOverride ?? _limits.ScannerTimeout;

        var languages = new Dictionary<string, LanguageAnalysisResult>();
        foreach (var analyzer in analyzers.Where(a => a.CanAnalyze(reader)))
        {
            var result = await analyzer.AnalyzeAsync(reader, cancellationToken);
            languages[analyzer.Descriptor.LanguageId] = result;
            logger.LogInformation(
                "Language {Language}: {Files} files, {Projects} projects, tier {Tier}.",
                result.LanguageId, result.Totals.FileCount, result.Projects.Count, result.TierAchieved);
        }

        var outcomes = new List<ScannerOutcome>();
        foreach (var (scannerId, scanId) in request.ScanIdsByScanner)
        {
            var scanner = scanners.FirstOrDefault(s => s.Descriptor.Id == scannerId);
            if (scanner is null)
            {
                outcomes.Add(ScannerOutcome.Failed(scannerId, $"Scanner '{scannerId}' is not registered in this process."));
                continue;
            }

            var sink = new ListSink();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(scannerTimeout);
            try
            {
                var result = await scanner.ExecuteAsync(new ScanContext
                {
                    AssessmentId = request.AssessmentId,
                    ScanId = scanId,
                    RepositoryKey = request.RepositoryKey,
                    Workspace = reader,
                    Languages = languages,
                    Findings = sink,
                    Today = request.Today,
                    History = request.History ?? [],
                }, timeout.Token);

                outcomes.Add(result.Succeeded
                    ? new ScannerOutcome(scannerId, true, null, sink.Candidates)
                    : ScannerOutcome.Failed(scannerId, result.Error ?? "Scanner reported failure without details."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Scanner {Scanner} timed out after {Timeout}.", scannerId, scannerTimeout);
                outcomes.Add(ScannerOutcome.Failed(scannerId, $"Scanner timed out after {scannerTimeout.TotalMinutes:0.#} minute(s)."));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scanner {Scanner} threw.", scannerId);
                outcomes.Add(ScannerOutcome.Failed(scannerId, ex.Message));
            }
        }

        return new WorkspaceScanOutcome(languages, outcomes);
    }

    private sealed class ListSink : IFindingSink
    {
        private readonly List<FindingCandidate> _candidates = [];

        public IReadOnlyList<FindingCandidate> Candidates => _candidates;

        public void Emit(FindingCandidate candidate) => _candidates.Add(candidate);
    }
}

/// <summary>Wire format between the worker and a scan-host child process (files, JSON).</summary>
public static class ScanWire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken)
            ?? throw new InvalidOperationException($"'{path}' did not contain a {typeof(T).Name}.");
    }
}
