namespace Atlas.Governance.Domain;

/// <summary>
/// One day of AI tool usage for one actor, one tool and one model, as reported by the opt-in developer CLI
/// ("an opt-in CLI the developer runs" is the only per-person collection path before the gate;
/// the actor label is self-declared by whoever runs the CLI — Atlas never derives identities).
/// Aggregates only: token counts, requests and sessions. Never prompts, paths or repository names.
/// Cost is an <see cref="CostBasis.Estimated"/> number from a versioned price catalog.
/// </summary>
public sealed class UsageFact
{
    public const int MaxActorLength = 120;
    public const int MaxToolLength = 40;
    public const int MaxModelLength = 120;

    private UsageFact()
    {
    }

    public UsageFact(
        Guid id,
        Guid tenantId,
        string actor,
        string tool,
        DateOnly period,
        string model,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        int requests,
        int sessions,
        decimal? estimatedCost,
        string priceCatalogVersion,
        string source)
    {
        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Actor, tool and model are required.");
        }

        if (inputTokens < 0 || outputTokens < 0 || cacheReadTokens < 0 || cacheWriteTokens < 0 || requests < 0 || sessions < 0)
        {
            throw new ArgumentException("Counts must not be negative.");
        }

        Id = id;
        TenantId = tenantId;
        Actor = Truncate(actor.Trim(), MaxActorLength);
        Tool = Truncate(tool.Trim().ToLowerInvariant(), MaxToolLength);
        Period = period;
        Model = Truncate(model.Trim(), MaxModelLength);
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CacheReadTokens = cacheReadTokens;
        CacheWriteTokens = cacheWriteTokens;
        Requests = requests;
        Sessions = sessions;
        EstimatedCost = estimatedCost;
        Currency = "USD";
        PriceCatalogVersion = priceCatalogVersion;
        Source = Truncate(source.Trim(), 40);
        ReportedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Self-declared label of whoever ran the CLI (a name, a handle, or an "anon-…" pseudonym).</summary>
    public string Actor { get; private set; } = null!;

    /// <summary>claude-code | codex | … (the local tool whose logs were read).</summary>
    public string Tool { get; private set; } = null!;

    public DateOnly Period { get; private set; }

    public string Model { get; private set; } = null!;

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long CacheReadTokens { get; private set; }

    public long CacheWriteTokens { get; private set; }

    public int Requests { get; private set; }

    public int Sessions { get; private set; }

    /// <summary>Null when the model is not in the price catalog (shown as "unpriced", never as zero cost).</summary>
    public decimal? EstimatedCost { get; private set; }

    public string Currency { get; private set; } = null!;

    public string PriceCatalogVersion { get; private set; } = null!;

    /// <summary>cli | import.</summary>
    public string Source { get; private set; } = null!;

    public DateTimeOffset ReportedAtUtc { get; private set; }

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>Applies a new estimate (after a price change). Returns true when the stored value moved.</summary>
    public bool Reprice(decimal? estimatedCost, string priceCatalogVersion)
    {
        if (EstimatedCost == estimatedCost && PriceCatalogVersion == priceCatalogVersion)
        {
            return false;
        }

        EstimatedCost = estimatedCost;
        PriceCatalogVersion = priceCatalogVersion;
        return true;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
