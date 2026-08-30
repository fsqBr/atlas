namespace Atlas.Domain.Findings;

public enum FindingCategory
{
    Code,
    Security,
    Secrets,
    Quality,
    Dependencies,
    Architecture,
    Data,
    Modernization,
}

public enum Severity
{
    Informational,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>How sure the producer is that the finding is real — separate from how bad it would be.</summary>
public enum ConfidenceLevel
{
    Low,
    Medium,
    High,
}

public enum FindingStatus
{
    Open,
    Resolved,

    /// <summary>Was resolved, has been seen again.</summary>
    Regressed,
    Suppressed,
    FalsePositive,
}

/// <summary>AI-produced artifacts must always be distinguishable from deterministic scanner output (.10).</summary>
public enum FindingOrigin
{
    Deterministic,
    AiEnriched,
    AiGenerated,
}
