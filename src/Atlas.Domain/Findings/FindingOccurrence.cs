namespace Atlas.Domain.Findings;

/// <summary>
/// One sighting of a finding in one scan: where it was, what the scanner said,
/// how confident it was. Immutable once written; partition-friendly by scan.
/// </summary>
public sealed class FindingOccurrence
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid FindingId { get; private set; }
    public Guid ScanId { get; private set; }
    public Severity Severity { get; private set; }
    public ConfidenceLevel Confidence { get; private set; }
    public string Message { get; private set; } = null!;
    public string? Remediation { get; private set; }
    public Evidence Evidence { get; private set; } = null!;
    public string? DataJson { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private FindingOccurrence()
    {
    }

    public FindingOccurrence(
        Guid id,
        Guid tenantId,
        Guid findingId,
        Guid scanId,
        Severity severity,
        ConfidenceLevel confidence,
        string message,
        string? remediation,
        Evidence evidence,
        string? dataJson)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Message must not be empty.", nameof(message));
        }

        Id = id;
        TenantId = tenantId;
        FindingId = findingId;
        ScanId = scanId;
        Severity = severity;
        Confidence = confidence;
        Message = message;
        Remediation = remediation;
        Evidence = evidence;
        DataJson = dataJson;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Where a finding was observed. Never contains a secret or
/// personal-data value: locations, symbols and hashes only (SECURITY.md).
/// </summary>
public sealed class Evidence
{
    public string? FilePath { get; private set; }
    public int? LineStart { get; private set; }
    public int? LineEnd { get; private set; }
    public string? Symbol { get; private set; }
    public string? SnippetHash { get; private set; }
    public string ScannerId { get; private set; } = null!;
    public string ScannerVersion { get; private set; } = null!;

    private Evidence()
    {
    }

    public Evidence(
        string scannerId,
        string scannerVersion,
        string? filePath = null,
        int? lineStart = null,
        int? lineEnd = null,
        string? symbol = null,
        string? snippetHash = null)
    {
        if (string.IsNullOrWhiteSpace(scannerId))
        {
            throw new ArgumentException("Scanner id must not be empty.", nameof(scannerId));
        }

        ScannerId = scannerId;
        ScannerVersion = scannerVersion;
        FilePath = filePath;
        LineStart = lineStart;
        LineEnd = lineEnd;
        Symbol = symbol;
        SnippetHash = snippetHash;
    }
}
