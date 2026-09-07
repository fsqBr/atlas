namespace Atlas.Governance.Domain;

/// <summary>
/// A tenant's own price for a model (or a family of models by regex), in USD per million tokens. Overrides the
/// builtin catalog entry that would otherwise match; the first override whose pattern matches wins, then the
/// builtin list. Every change reprices the tenant's stored usage estimates so history and new reports agree.
/// </summary>
public sealed class ModelPriceOverride
{
    public const int MaxPatternLength = 200;

    private ModelPriceOverride()
    {
    }

    public ModelPriceOverride(Guid id, Guid tenantId, string pattern, decimal input, decimal output, decimal? cacheRead, decimal? cacheWrite, string? note, string updatedBy)
    {
        Id = id;
        TenantId = tenantId;
        Pattern = Normalize(pattern);
        Set(input, output, cacheRead, cacheWrite, note, updatedBy);
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Regex matched against the model id (case-insensitive). A plain model id works as-is.</summary>
    public string Pattern { get; private set; } = null!;

    public decimal Input { get; private set; }

    public decimal Output { get; private set; }

    public decimal? CacheRead { get; private set; }

    public decimal? CacheWrite { get; private set; }

    public string? Note { get; private set; }

    public string UpdatedBy { get; private set; } = null!;

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Set(decimal input, decimal output, decimal? cacheRead, decimal? cacheWrite, string? note, string updatedBy)
    {
        if (input < 0 || output < 0 || cacheRead is < 0 || cacheWrite is < 0)
        {
            throw new ArgumentException("Prices must not be negative.");
        }

        if (input > 10_000 || output > 10_000 || cacheRead is > 10_000 || cacheWrite is > 10_000)
        {
            throw new ArgumentException("Prices are USD per million tokens; a value above 10000 is almost certainly a unit mistake.");
        }

        Input = input;
        Output = output;
        CacheRead = cacheRead;
        CacheWrite = cacheWrite;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 200)];
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "web" : updatedBy.Trim()[..Math.Min(updatedBy.Trim().Length, 100)];
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public static string Normalize(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("A model id or pattern is required.", nameof(pattern));
        }

        var p = pattern.Trim();
        if (p.Length > MaxPatternLength)
        {
            throw new ArgumentException($"Pattern longer than {MaxPatternLength} characters.", nameof(pattern));
        }

        try
        {
            _ = new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"'{p}' is not a valid pattern: {ex.Message}", nameof(pattern), ex);
        }

        return p;
    }
}
