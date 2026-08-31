using Atlas.Domain.Findings;

namespace Atlas.Scanner.Dependencies.Vulnerabilities;

/// <summary>
/// One home for mapping an OSV severity string to <see cref="Severity"/>, shared by every
/// ecosystem scanner (NuGet/npm, Maven, PyPI). Label severities map directly; a CVSS vector
/// string is scored (a 9.8 vector must not land on Medium); anything unreadable stays a
/// conservative Medium. Keeping this in one place stops the per-scanner copies from drifting —
/// a fix here reaches every ecosystem at once.
/// </summary>
public static class OsvSeverity
{
    public static Severity Map(string? severity) =>
        severity?.Trim().ToUpperInvariant() switch
        {
            "CRITICAL" => Severity.Critical,
            "HIGH" => Severity.High,
            "MODERATE" or "MEDIUM" => Severity.Medium,
            "LOW" => Severity.Low,
            { } vector when vector.StartsWith("CVSS:", StringComparison.Ordinal) => CvssVector.ToSeverity(vector) ?? Severity.Medium,
            _ => Severity.Medium,
        };
}
