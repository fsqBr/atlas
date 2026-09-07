using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas.Governance.Application;

public sealed record ModelPrice(string Pattern, decimal Input, decimal Output, decimal? CacheRead = null, decimal? CacheWrite = null);

public sealed record PriceCatalogDocument(string Version, string Currency, string? Note, IReadOnlyList<ModelPrice> Models);

/// <summary>
/// Versioned list prices per million tokens. Every estimate records the catalog version it came
/// from; a model the catalog does not know yields <c>null</c> — shown as unpriced, never as zero.
/// </summary>
public sealed class PriceCatalog
{
    public const string ResourceName = "Atlas.Governance.Application.ai-prices.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private readonly IReadOnlyList<(ModelPrice Price, Regex Regex)> _prices;

    private PriceCatalog(PriceCatalogDocument document, string source)
    {
        Document = document;
        Source = source;
        _prices = document.Models.Select(m => (m, new Regex(m.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))).ToList();
    }

    public PriceCatalogDocument Document { get; }

    public string Version => Document.Version;

    public string Currency => Document.Currency;

    public string Source { get; }

    public static PriceCatalog LoadBuiltin()
    {
        using var stream = typeof(PriceCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded price catalog '{ResourceName}' is missing.");
        return Load(stream, "builtin");
    }

    public static PriceCatalog LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, path);
    }

    public static PriceCatalog Load(Stream stream, string source)
    {
        var document = JsonSerializer.Deserialize<PriceCatalogDocument>(stream, Json) ?? throw new InvalidOperationException("The price catalog is empty.");
        if (string.IsNullOrWhiteSpace(document.Version) || document.Models is not { Count: > 0 })
        {
            throw new InvalidOperationException("The price catalog needs a version and at least one model.");
        }

        foreach (var m in document.Models)
        {
            if (m.Input < 0 || m.Output < 0 || m.CacheRead is < 0 || m.CacheWrite is < 0)
            {
                throw new InvalidOperationException($"Negative price for '{m.Pattern}'.");
            }

            _ = new Regex(m.Pattern); // throws on an invalid pattern
        }

        return new PriceCatalog(document with { Currency = string.IsNullOrWhiteSpace(document.Currency) ? "USD" : document.Currency }, source);
    }

    public ModelPrice? Find(string model) => _prices.FirstOrDefault(p => p.Regex.IsMatch(model)).Price;

    /// <summary>Cost in catalog currency, or null when the model is unknown. Cache tokens without a cache price fall back to the input price.</summary>
    public decimal? Estimate(string model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens)
    {
        var price = Find(model);
        if (price is null)
        {
            return null;
        }

        var cost = inputTokens * price.Input + outputTokens * price.Output
            + cacheReadTokens * (price.CacheRead ?? price.Input)
            + cacheWriteTokens * (price.CacheWrite ?? price.Input);
        return decimal.Round(cost / 1_000_000m, 4);
    }
}
