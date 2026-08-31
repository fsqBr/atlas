using System.Text.Json;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Scanner.Abstractions;

namespace Atlas.Application.Findings;

public sealed record ReconciliationResult(
    IReadOnlyList<Finding> Created,
    IReadOnlyList<FindingOccurrence> Occurrences,
    int Recurring,
    int Regressed,
    int Resolved);

/// <summary>
/// Applies the design notes lifecycle to one scan's candidates: fingerprint each, match
/// against existing findings (new / recurring / regressed), record an occurrence
/// per candidate, and — only when the scan succeeded — resolve the scanner's own
/// findings that were not seen. Pure: no I/O, fully unit-testable.
/// </summary>
public static class FindingReconciler
{
    public static ReconciliationResult Reconcile(
        Guid tenantId,
        Guid assessmentId,
        Guid scanId,
        string scannerId,
        string scannerVersion,
        string repositoryKey,
        IReadOnlyList<FindingCandidate> candidates,
        IReadOnlyDictionary<string, RuleDefinition> rules,
        IReadOnlyList<Finding> existing,
        bool scanSucceeded)
    {
        var byFingerprint = existing.ToDictionary(f => f.Fingerprint, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var created = new List<Finding>();
        var occurrences = new List<FindingOccurrence>();
        int recurring = 0, regressed = 0;

        foreach (var candidate in candidates)
        {
            if (!rules.TryGetValue(candidate.RuleId, out var rule))
            {
                throw new InvalidOperationException(
                    $"Scanner '{scannerId}' emitted undeclared rule '{candidate.RuleId}'. Declare it in IScanner.Rules.");
            }

            var fingerprint = FindingFingerprint.Compute(
                candidate.RuleId,
                rule.MajorVersion,
                repositoryKey,
                candidate.Evidence.FilePath,
                candidate.Evidence.Symbol ?? candidate.Evidence.SnippetHash);

            if (!byFingerprint.TryGetValue(fingerprint, out var finding))
            {
                finding = Finding.Create(
                    Guid.NewGuid(), tenantId, assessmentId, fingerprint,
                    candidate.RuleId, rule.Category, candidate.Severity, candidate.Title,
                    FindingOrigin.Deterministic, scanId);

                byFingerprint[fingerprint] = finding;
                created.Add(finding);
            }
            else if (seen.Add(fingerprint))
            {
                var wasResolved = finding.Status == FindingStatus.Resolved;
                finding.Seen(scanId, candidate.Severity, candidate.Title);
                if (wasResolved)
                {
                    regressed++;
                }
                else
                {
                    recurring++;
                }
            }
            else if (candidate.Severity > finding.Severity)
            {
                // Another candidate of this scan already touched the finding: keep the worst severity
                // (e.g. a Critical credential leak sharing the fingerprint of a High PII leak).
                finding.Seen(scanId, candidate.Severity, candidate.Title);
            }

            seen.Add(fingerprint);

            occurrences.Add(new FindingOccurrence(
                Guid.NewGuid(),
                tenantId,
                finding.Id,
                scanId,
                candidate.Severity,
                candidate.Confidence,
                candidate.Message,
                candidate.Remediation,
                new Evidence(
                    scannerId,
                    scannerVersion,
                    candidate.Evidence.FilePath,
                    candidate.Evidence.LineStart,
                    candidate.Evidence.LineEnd,
                    candidate.Evidence.Symbol,
                    candidate.Evidence.SnippetHash),
                candidate.Data is null ? null : JsonSerializer.Serialize(candidate.Data)));
        }

        var resolved = 0;
        if (scanSucceeded)
        {
            foreach (var finding in existing.Where(f => !seen.Contains(f.Fingerprint)))
            {
                if (finding.TryResolve(scanId))
                {
                    resolved++;
                }
            }
        }

        return new ReconciliationResult(created, occurrences, recurring, regressed, resolved);
    }
}
