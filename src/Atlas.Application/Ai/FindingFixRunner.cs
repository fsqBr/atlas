using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Application.Workspaces;
using Atlas.Domain.Ai;
using Atlas.Domain.Findings;
using Atlas.Domain.Jobs;
using Atlas.Domain.Workspaces;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Ai;

/// <summary>What the fix job carries: which finding, in which language the answer is wanted.</summary>
public sealed record FindingFixRequest(Guid FindingId, string Lang);

public sealed class FixNotEligibleException(string message) : Exception(message);

/// <summary>
/// Which findings may be sent to the model for a patch. Secrets never leave the
/// environment (the value is the finding), binaries cannot be patched, and a
/// finding without a location has nothing to point the model at.
/// </summary>
public static class FindingFixEligibility
{
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".zip", ".nupkg", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".pdf", ".bin", ".dat", ".db", ".mdf", ".bak",
    };

    /// <summary>Null when eligible; otherwise the reason, safe to show.</summary>
    public static string? Reject(string ruleId, FindingCategory category, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath == "estate")
        {
            return "This finding has no file location, so there is nothing to patch.";
        }

        if (category == FindingCategory.Secrets || ruleId.StartsWith("secrets.", StringComparison.Ordinal))
        {
            return "Secrets findings are never sent to a model: rotate the credential and move it to a secret store instead.";
        }

        if (BinaryExtensions.Contains(Path.GetExtension(filePath)))
        {
            return "The location is a binary artifact; a source patch does not apply.";
        }

        return null;
    }
}

/// <summary>
/// Belt and braces before a snippet leaves the environment: the values of
/// obvious credentials, private key blocks, bearer tokens and long opaque tokens
/// become "***". The scanners already keep secrets findings out; this covers a
/// password that happens to sit next to the code being fixed.
/// </summary>
public static partial class SnippetRedactor
{
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|apikey|client[_-]?secret|access[_-]?key|accountkey|sharedaccesskey|private[_-]?key)\b(\s*[=:]\s*)([""']?)([^""';,\s)]+)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._\-]{16,}")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\b(sk|ghp|gho|xox[abp]|atlas_pat)[_-][A-Za-z0-9_\-]{12,}\b")]
    private static partial Regex KnownPrefixes();

    public static string Redact(string text, out int replacements)
    {
        var count = 0;
        string Count(string replaced)
        {
            count++;
            return replaced;
        }

        var result = PrivateKey().Replace(text, _ => Count("-----BEGIN PRIVATE KEY-----***-----END PRIVATE KEY-----"));
        result = KeyValue().Replace(result, m => Count($"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}***"));
        result = Bearer().Replace(result, _ => Count("Bearer ***"));
        result = AwsKey().Replace(result, m => Count($"{m.Groups[1].Value}***"));
        result = KnownPrefixes().Replace(result, m => Count($"{m.Groups[1].Value}_***"));
        replacements = count;
        return result;
    }
}

/// <summary>The lines around the finding, numbered so the model's diff can be checked against the file.</summary>
public sealed record FixSnippet(string FilePath, int FirstLine, int LastLine, int TotalLines, string Text, bool Truncated);

public static class FixSnippets
{
    public const int ContextLines = 25;
    public const int MaxLines = 120;
    public const int MaxChars = 6000;

    public static FixSnippet Extract(string filePath, string content, int? lineStart, int? lineEnd)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var total = lines.Length;
        var focusStart = Math.Clamp(lineStart ?? 1, 1, Math.Max(1, total));
        var focusEnd = Math.Clamp(lineEnd ?? focusStart, focusStart, Math.Max(1, total));
        var first = Math.Max(1, focusStart - ContextLines);
        var last = Math.Min(total, focusEnd + ContextLines);
        if (last - first + 1 > MaxLines)
        {
            // keep the flagged lines centred, shrink the context symmetrically
            var spare = MaxLines - (focusEnd - focusStart + 1);
            var before = Math.Max(0, spare / 2);
            first = Math.Max(1, focusStart - before);
            last = Math.Min(total, first + MaxLines - 1);
        }

        var sb = new StringBuilder();
        var truncated = false;
        var width = last.ToString().Length;
        for (var n = first; n <= last; n++)
        {
            var line = lines[n - 1];
            if (sb.Length + line.Length + width + 3 > MaxChars)
            {
                truncated = true;
                last = n - 1;
                break;
            }

            sb.Append(n.ToString().PadLeft(width)).Append(": ").Append(line).Append('\n');
        }

        return new FixSnippet(filePath, first, Math.Max(first, last), total, sb.ToString().TrimEnd('\n'), truncated);
    }
}

/// <summary>
/// Worker side of "Suggest a fix with AI": materializes the source, cuts the
/// snippet around the finding, redacts credentials, asks the model for a
/// diagnosis and a unified diff, and stores the answer as a narrative keyed by the
/// finding's fingerprint. Only the snippet travels — never the repository.
/// </summary>
public sealed class FindingFixRunner(
    IAssessmentRepository assessments,
    IFindingRepository findings,
    IRuleCatalog rules,
    IAiNarrativeRepository narratives,
    AiSettingsService aiSettings,
    IWorkspaceManager workspaces,
    IUnitOfWork unitOfWork,
    ILogger<FindingFixRunner> logger)
{
    public async Task RunAsync(Guid assessmentId, string? payload, CancellationToken cancellationToken)
    {
        var request = Parse(payload) ?? throw new InvalidOperationException("The fix job does not say which finding to patch.");
        var language = AiNarrative.NormalizeLang(request.Lang);
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");
        var finding = await findings.GetAsync(request.FindingId, cancellationToken);
        if (finding is null || finding.AssessmentId != assessmentId)
        {
            throw new KeyNotFoundException($"Finding {request.FindingId} not found.");
        }

        var latest = await findings.GetLatestOccurrenceAsync(finding.Id, cancellationToken)
            ?? throw new InvalidOperationException("The finding has no recorded occurrence.");
        var rejection = FindingFixEligibility.Reject(finding.RuleId, finding.Category, latest.Evidence.FilePath);
        if (rejection is not null)
        {
            throw new FixNotEligibleException(rejection);
        }

        var (settings, client) = await aiSettings.ResolveClientAsync(cancellationToken)
            ?? throw new InvalidOperationException("AI analysis is disabled or has no usable provider; configure it under Settings → AI.");

        Workspace? workspace = null;
        try
        {
            workspace = await workspaces.PrepareAsync(assessment.Source, cancellationToken);
            var reader = new WorkspaceReader(workspace.RootPath);
            var path = latest.Evidence.FilePath!.Replace('\\', '/');
            if (!reader.Exists(path))
            {
                throw new InvalidOperationException($"'{path}' is not in the current source (renamed or removed since the run?). Run the assessment again first.");
            }

            var content = await reader.ReadAllTextAsync(path, cancellationToken);
            var snippet = FixSnippets.Extract(path, content, latest.Evidence.LineStart, latest.Evidence.LineEnd);
            var redacted = SnippetRedactor.Redact(snippet.Text, out var redactions);
            var catalog = await rules.GetAllAsync(cancellationToken);
            var rule = catalog.GetValueOrDefault(finding.RuleId);
            var prompt = BuildPrompt(language, finding.RuleId, FindingLocalizer.RuleTitle(rule, finding.RuleId, language), FindingLocalizer.RuleDescription(rule, language),
                latest.Message, latest.Remediation, snippet with { Text = redacted }, latest.Evidence.LineStart, latest.Evidence.LineEnd, redactions);

            var reply = await client.CompleteAsync(new ChatRequest(
                "You are a senior .NET engineer proposing a minimal, reviewable patch for one static-analysis finding. You only see the snippet shown; never invent code outside it, never touch redacted values, never weaken security to silence a rule.",
                prompt, MaxTokens: 2500, Temperature: 0.1), cancellationToken);
            var text = reply.Text.Trim();
            if (text.Length == 0)
            {
                throw new ChatProviderException($"{client.Provider} returned an empty answer.");
            }

            var existing = await narratives.GetAsync(assessmentId, AiNarrative.Kinds.FindingFix, finding.Fingerprint, language, cancellationToken);
            if (existing is null)
            {
                narratives.Add(new AiNarrative(Guid.NewGuid(), assessment.TenantId, assessmentId, AiNarrative.Kinds.FindingFix, finding.Fingerprint, language, text, reply.Model, reply.InputTokens, reply.OutputTokens));
            }
            else
            {
                existing.Replace(text, reply.Model, reply.InputTokens, reply.OutputTokens);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Fix suggested for finding {FindingId} ({Rule}) via {Provider}/{Model}: {Lines} lines sent, {Redactions} redaction(s), {In}/{Out} tokens.",
                finding.Id, finding.RuleId, settings.Provider, reply.Model, snippet.LastLine - snippet.FirstLine + 1, redactions, reply.InputTokens, reply.OutputTokens);
        }
        finally
        {
            if (workspace is not null)
            {
                await workspaces.ReleaseAsync(workspace.Id, CancellationToken.None);
            }
        }
    }

    public static FindingFixRequest? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FindingFixRequest>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string BuildPrompt(string language, string ruleId, string ruleTitle, string? ruleDescription, string message, string? remediation,
        FixSnippet snippet, int? lineStart, int? lineEnd, int redactions)
    {
        var pt = language == "pt-BR";
        var sb = new StringBuilder();
        sb.AppendLine($"Rule: {ruleId} — {ruleTitle}");
        if (!string.IsNullOrWhiteSpace(ruleDescription))
        {
            sb.AppendLine($"Rule description: {ruleDescription}");
        }

        sb.AppendLine($"Finding: {message}");
        if (!string.IsNullOrWhiteSpace(remediation))
        {
            sb.AppendLine($"Generic remediation from the rule: {remediation}");
        }

        var flagged = lineStart is null ? "unknown line" : lineEnd is { } e && e != lineStart ? $"lines {lineStart}–{e}" : $"line {lineStart}";
        sb.AppendLine($"File: {snippet.FilePath} ({flagged} flagged; the snippet shows lines {snippet.FirstLine}–{snippet.LastLine} of {snippet.TotalLines}{(snippet.Truncated ? ", truncated" : "")}).");
        if (redactions > 0)
        {
            sb.AppendLine($"{redactions} credential value(s) in the snippet were replaced by *** before sending; leave them as they are.");
        }

        sb.AppendLine();
        sb.AppendLine("Snippet (line-numbered; the numbers are not part of the file):");
        sb.AppendLine("```");
        sb.AppendLine(snippet.Text);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine(pt
            ? "Responda em português do Brasil, em Markdown, exatamente com estas seções:\n"
              + "## Diagnóstico — 2 a 3 frases: o que está errado nestas linhas e por que importa.\n"
              + $"## Correção — UM diff unificado dentro de um bloco ```diff, com cabeçalho `--- a/{snippet.FilePath}` e `+++ b/{snippet.FilePath}` e hunks mínimos restritos às linhas mostradas; preserve indentação e estilo. Se não for possível corrigir com segurança só com o trecho, gere um diff que adiciona um comentário TODO explicando o que falta e diga isso.\n"
              + "## Observações — efeitos colaterais, testes a rodar, o que o revisor precisa checar. Sem inventar APIs que não estejam visíveis ou na BCL."
            : "Answer in English, in Markdown, with exactly these sections:\n"
              + "## Diagnosis — 2–3 sentences: what is wrong in these lines and why it matters.\n"
              + $"## Patch — ONE unified diff inside a ```diff fence, with `--- a/{snippet.FilePath}` and `+++ b/{snippet.FilePath}` headers and minimal hunks limited to the lines shown; keep indentation and style. If a safe fix is not possible from the snippet alone, produce a diff that adds a TODO comment explaining what is missing and say so.\n"
              + "## Notes — side effects, tests to run, what the reviewer must check. Never invent APIs that are not visible or in the BCL.");
        return sb.ToString();
    }
}

/// <summary>API side: eligibility and AI checks up front, then one job on the shared queue.</summary>
public sealed class QueueFindingFixHandler(
    IAssessmentRepository assessments,
    IFindingRepository findings,
    IAiSettingsRepository aiSettings,
    IScanJobQueue queue,
    IUnitOfWork unitOfWork)
{
    public async Task<Guid> HandleAsync(Guid assessmentId, Guid findingId, string? lang, CancellationToken cancellationToken)
    {
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");
        var finding = await findings.GetAsync(findingId, cancellationToken);
        if (finding is null || finding.AssessmentId != assessmentId)
        {
            throw new KeyNotFoundException($"Finding {findingId} not found.");
        }

        var settings = await aiSettings.GetAsync(assessment.TenantId, cancellationToken);
        if (settings is null || !settings.IsUsable)
        {
            throw new AiNotConfiguredException();
        }

        var latest = await findings.GetLatestOccurrenceAsync(finding.Id, cancellationToken);
        var rejection = FindingFixEligibility.Reject(finding.RuleId, finding.Category, latest?.Evidence.FilePath);
        if (rejection is not null)
        {
            throw new FixNotEligibleException(rejection);
        }

        if (await queue.HasActiveJobAsync(assessmentId, cancellationToken))
        {
            throw new InvalidOperationException("A job is already queued or in progress for this assessment.");
        }

        var payload = JsonSerializer.Serialize(new FindingFixRequest(finding.Id, AiNarrative.NormalizeLang(lang)));
        var job = new ScanJob(Guid.NewGuid(), assessment.TenantId, assessment.Id, ScanJob.Kinds.FindingFix, payload);
        queue.Enqueue(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return job.Id;
    }
}
