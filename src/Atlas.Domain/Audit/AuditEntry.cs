namespace Atlas.Domain.Audit;

/// <summary>
/// One state-changing API call: who did what, to which resource, with which
/// outcome. Append-only; never carries request bodies (secrets, personal data)
/// — only method, path, status and an optional short detail.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; private set; }
    public Guid TenantId { get; private set; }
    public DateTimeOffset AtUtc { get; private set; }
    public string Actor { get; private set; } = null!;
    public string Method { get; private set; } = null!;
    public string Path { get; private set; } = null!;
    public int StatusCode { get; private set; }
    public Guid? AssessmentId { get; private set; }
    public string? Detail { get; private set; }
    public string? ClientIp { get; private set; }

    private AuditEntry()
    {
    }

    public AuditEntry(Guid tenantId, string actor, string method, string path, int statusCode, Guid? assessmentId, string? detail, string? clientIp)
    {
        TenantId = tenantId;
        AtUtc = DateTimeOffset.UtcNow;
        Actor = Truncate(string.IsNullOrWhiteSpace(actor) ? "anonymous" : actor, 200);
        Method = Truncate(method, 10);
        Path = Truncate(path, 500);
        StatusCode = statusCode;
        AssessmentId = assessmentId;
        Detail = detail is null ? null : Truncate(detail, 500);
        ClientIp = clientIp is null ? null : Truncate(clientIp, 64);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
