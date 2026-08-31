using System.Text;
using System.Text.Json;
using Atlas.Domain.Ai;
using Atlas.Language.Abstractions;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Ai;

public sealed record ExtractedRule(
    string FilePath,
    string Symbol,
    int StartLine,
    string Name,
    string DescriptionEn,
    string DescriptionPt,
    BusinessRuleCategory Category,
    IReadOnlyList<string> Conditions,
    double Confidence);

public sealed record ExtractionOutcome(IReadOnlyList<ExtractedRule> Rules, int SnippetsSent, long InputTokens, long OutputTokens, int FailedBatches);

/// <summary>
/// Turns code snippets into business rules through the configured model. Snippets
/// are batched (a few methods per request), the model must answer with JSON only,
/// and anything that does not parse is dropped rather than guessed. Every rule
/// keeps the file/member it came from so a reader can verify it.
/// </summary>
public sealed class BusinessRuleExtractor(ILogger<BusinessRuleExtractor> logger)
{
    public const int SnippetsPerBatch = 4;
    public const int MaxBatchChars = 14_000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<ExtractionOutcome> ExtractAsync(IChatClient client, IReadOnlyList<BusinessRuleCandidate> candidates, CancellationToken cancellationToken)
    {
        var rules = new List<ExtractedRule>();
        long input = 0, output = 0;
        var sent = 0;
        var failed = 0;

        foreach (var batch in Batch(candidates))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ChatRequest(BusinessRulePrompts.System, BusinessRulePrompts.User(batch), MaxTokens: 4096, Temperature: 0.1);
            try
            {
                var result = await client.CompleteAsync(request, cancellationToken);
                input += result.InputTokens;
                output += result.OutputTokens;
                sent += batch.Count;
                rules.AddRange(Parse(result.Text, batch));
            }
            catch (ChatProviderException ex) when (ex.StatusCode is 401 or 403)
            {
                throw; // a bad key will not get better on the next batch
            }
            catch (Exception ex) when (ex is ChatProviderException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failed++;
                logger.LogWarning(ex, "Business rule batch of {Count} snippets failed; continuing.", batch.Count);
                if (failed >= 3 && sent == 0)
                {
                    throw new ChatProviderException($"The provider failed {failed} times in a row: {ex.Message}");
                }
            }
        }

        return new ExtractionOutcome(rules, sent, input, output, failed);
    }

    public static IEnumerable<IReadOnlyList<BusinessRuleCandidate>> Batch(IReadOnlyList<BusinessRuleCandidate> candidates)
    {
        var current = new List<BusinessRuleCandidate>();
        var chars = 0;
        foreach (var candidate in candidates)
        {
            if (current.Count > 0 && (current.Count >= SnippetsPerBatch || chars + candidate.Snippet.Length > MaxBatchChars))
            {
                yield return current;
                current = [];
                chars = 0;
            }

            current.Add(candidate);
            chars += candidate.Snippet.Length;
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    /// <summary>Parses the model's JSON; tolerates code fences and prose around the array; drops malformed items.</summary>
    public static IReadOnlyList<ExtractedRule> Parse(string text, IReadOnlyList<BusinessRuleCandidate> batch)
    {
        var json = ExtractJsonArray(text);
        if (json is null)
        {
            return [];
        }

        List<RawRule>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<RawRule>>(json, Json);
        }
        catch (JsonException)
        {
            return [];
        }

        if (raw is null)
        {
            return [];
        }

        var result = new List<ExtractedRule>();
        foreach (var r in raw)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.DescriptionEn))
            {
                continue;
            }

            var index = r.Snippet is { } n && n >= 1 && n <= batch.Count ? n - 1 : -1;
            var source = index >= 0 ? batch[index] : batch.FirstOrDefault(c => r.Symbol is not null && c.Symbol.Equals(r.Symbol, StringComparison.OrdinalIgnoreCase)) ?? batch[0];
            var category = Enum.TryParse<BusinessRuleCategory>(r.Category ?? "", ignoreCase: true, out var c) ? c : BusinessRuleCategory.Other;
            var confidence = r.Confidence is { } conf ? Math.Clamp(conf, 0, 1) : 0.5;
            result.Add(new ExtractedRule(
                source.FilePath, source.Symbol, source.StartLine,
                r.Name.Trim(), r.DescriptionEn.Trim(), string.IsNullOrWhiteSpace(r.DescriptionPt) ? r.DescriptionEn.Trim() : r.DescriptionPt.Trim(),
                category, (r.Conditions ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(12).ToList(), confidence));
        }

        return result;
    }

    internal static string? ExtractJsonArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            // Some models wrap in an object: {"rules":[...]}
            var objStart = text.IndexOf('{');
            var objEnd = text.LastIndexOf('}');
            if (objStart < 0 || objEnd <= objStart)
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(text[objStart..(objEnd + 1)]);
                if (doc.RootElement.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                {
                    return rules.GetRawText();
                }
            }
            catch (JsonException)
            {
            }

            return null;
        }

        return text[start..(end + 1)];
    }

    private sealed record RawRule(
        int? Snippet,
        string? Symbol,
        string? Name,
        string? DescriptionEn,
        string? DescriptionPt,
        string? Category,
        List<string>? Conditions,
        double? Confidence);
}

public static class BusinessRulePrompts
{
    public const string System = """
        You are a senior software analyst recovering BUSINESS RULES from legacy C# code for a modernization assessment.
        A business rule is a decision the business cares about: validation, eligibility, calculation/pricing, workflow state change, authorization, data integrity.
        Do NOT report technical plumbing (null checks, logging, mapping, retries, framework wiring).
        Answer with a JSON array ONLY — no prose, no markdown fences. Each item:
        {"snippet": <1-based index of the snippet>, "symbol": "<Type.Method as given>", "name": "<short rule name>",
         "descriptionEn": "<1-3 sentences in English, business language, no code>", "descriptionPt": "<the same in Brazilian Portuguese>",
         "category": "Validation|Calculation|Eligibility|Pricing|Workflow|Authorization|DataIntegrity|Other",
         "conditions": ["<condition or threshold as the business would state it>", ...], "confidence": <0.0-1.0>}
        Report at most 4 rules per snippet, only rules actually present in the code. If a snippet has no business rule, report nothing for it. Return [] when there is nothing.
        """;

    public static string User(IReadOnlyList<BusinessRuleCandidate> batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"There are {batch.Count} code snippets. Extract the business rules.");
        for (var i = 0; i < batch.Count; i++)
        {
            var c = batch[i];
            sb.AppendLine();
            sb.AppendLine($"### Snippet {i + 1} — {c.Symbol} ({c.FilePath}:{c.StartLine})");
            sb.AppendLine("```csharp");
            sb.AppendLine(c.Snippet);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }
}
