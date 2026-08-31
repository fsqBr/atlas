using System.Text;
using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Domain.Ai;
using Atlas.Domain.Assessments;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Ai;

public sealed record NarrativeResult(string Text, string Model, bool Cached, DateTimeOffset CreatedAtUtc, int? Rating = null, string? FeedbackComment = null)
{
    public static NarrativeResult FromStored(AiNarrative n) => new(n.Text, n.Model, true, n.CreatedAtUtc, n.Rating, n.FeedbackComment);
}

/// <summary>Rough, honest pre-flight numbers for one business-rule analysis (no materialization needed).</summary>
public sealed record AiCostEstimate(int Methods, int Requests, long InputTokens, long OutputTokens, string Note);

/// <summary>
/// Narratives on demand: finding explanations, the executive summary
/// and the migration plan draft. The model receives rule/finding metadata or the
/// figures Atlas computed — never source code — and answers in the reader's
/// language. Answers are cached per key and language.
/// </summary>
public sealed class AiNarrativeService(
    IAiNarrativeRepository narratives,
    IAssessmentRepository assessments,
    IFindingRepository findings,
    IRuleCatalog rules,
    AiSettingsService aiSettings,
    IUnitOfWork unitOfWork,
    ILogger<AiNarrativeService> logger)
{
    public const int TokensPerSnippetIn = 1_100;
    public const int TokensPerRequestOverhead = 650;
    public const int TokensPerSnippetOut = 180;

    public static AiCostEstimate Estimate(int methods, int snippetsPerBatch)
    {
        methods = Math.Max(0, methods);
        var requests = (int)Math.Ceiling(methods / (double)Math.Max(1, snippetsPerBatch));
        return new AiCostEstimate(
            methods,
            requests,
            (long)methods * TokensPerSnippetIn + (long)requests * TokensPerRequestOverhead,
            (long)methods * TokensPerSnippetOut,
            "Rough figures: ~1.1k input tokens per method (6k-char cap), ~650 per request of prompt, ~180 output tokens per method. Actual usage is recorded on each analysis.");
    }

    public async Task<NarrativeResult> ExplainFindingAsync(Guid assessmentId, Guid findingId, string? lang, bool refresh, CancellationToken cancellationToken)
    {
        var language = AiNarrative.NormalizeLang(lang);
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");
        var finding = await findings.GetAsync(findingId, cancellationToken);
        if (finding is null || finding.AssessmentId != assessmentId)
        {
            throw new KeyNotFoundException($"Finding {findingId} not found.");
        }

        var existing = await narratives.GetAsync(assessmentId, AiNarrative.Kinds.FindingExplanation, finding.Fingerprint, language, cancellationToken);
        if (existing is not null && !refresh)
        {
            return NarrativeResult.FromStored(existing);
        }

        var resolved = await aiSettings.ResolveClientAsync(cancellationToken) ?? throw new AiNotConfiguredException();
        var catalog = await rules.GetAllAsync(cancellationToken);
        var rule = catalog.GetValueOrDefault(finding.RuleId);
        var latest = await findings.GetLatestOccurrenceAsync(finding.Id, cancellationToken);

        var prompt = new StringBuilder();
        prompt.AppendLine($"Rule: {finding.RuleId} — {FindingLocalizer.RuleTitle(rule, finding.RuleId, language)}");
        prompt.AppendLine($"Category: {finding.Category}; severity: {latest?.Severity ?? finding.Severity}; status: {finding.Status}.");
        var description = FindingLocalizer.RuleDescription(rule, language);
        if (!string.IsNullOrWhiteSpace(description))
        {
            prompt.AppendLine($"Rule description: {description}");
        }

        if (latest is not null)
        {
            prompt.AppendLine($"Finding message: {latest.Message}");
            if (!string.IsNullOrWhiteSpace(latest.Remediation))
            {
                prompt.AppendLine($"Suggested remediation (generic): {latest.Remediation}");
            }

            var e = latest.Evidence;
            if (e.FilePath is not null)
            {
                prompt.AppendLine($"Location: {e.FilePath}{(e.LineStart is { } l ? $":{l}" : "")}{(e.Symbol is not null ? $" ({e.Symbol})" : "")}");
            }
        }

        prompt.AppendLine($"Assessment: {assessment.Name} (.NET codebase under modernization assessment).");
        prompt.AppendLine();
        prompt.AppendLine(language == "pt-BR"
            ? "Explique em português do Brasil, para um líder técnico: (1) por que isso importa neste contexto, (2) o que fazer concretamente para corrigir, (3) esforço estimado (baixo/médio/alto) e em que ordem atacar se houver muitas ocorrências. Máximo de 3 parágrafos curtos, sem código extenso, sem repetir o enunciado."
            : "Explain in English, for a tech lead: (1) why this matters in this context, (2) what to do concretely to fix it, (3) estimated effort (low/medium/high) and how to sequence it if there are many occurrences. At most 3 short paragraphs, no long code, do not restate the finding.");

        var (settings, client) = resolved;
        var reply = await client.CompleteAsync(new ChatRequest(
            "You are a senior .NET modernization consultant. Be specific, practical and honest about uncertainty. You only know what the prompt states; do not invent file contents.",
            prompt.ToString(), MaxTokens: 900, Temperature: 0.2), cancellationToken);

        var text = reply.Text.Trim();
        if (text.Length == 0)
        {
            throw new ChatProviderException($"{client.Provider} returned an empty explanation.");
        }

        if (existing is null)
        {
            existing = new AiNarrative(Guid.NewGuid(), assessment.TenantId, assessmentId, AiNarrative.Kinds.FindingExplanation, finding.Fingerprint, language, text, reply.Model, reply.InputTokens, reply.OutputTokens);
            narratives.Add(existing);
        }
        else
        {
            existing.Replace(text, reply.Model, reply.InputTokens, reply.OutputTokens);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Explained finding {FindingId} ({Rule}) via {Provider}/{Model} in {Lang}.", findingId, finding.RuleId, settings.Provider, reply.Model, language);
        return new NarrativeResult(text, reply.Model, false, existing.CreatedAtUtc);
    }

    public async Task<NarrativeResult?> GetSummaryAsync(Guid assessmentId, string? lang, CancellationToken cancellationToken)
    {
        var existing = await narratives.GetAsync(assessmentId, AiNarrative.Kinds.ExecutiveSummary, "summary", AiNarrative.NormalizeLang(lang), cancellationToken);
        return existing is null ? null : NarrativeResult.FromStored(existing);
    }

    /// <summary>Stores a summary produced from report facts (the reporting layer builds the facts; this layer talks to the model).</summary>
    public async Task<NarrativeResult> WriteSummaryAsync(Guid assessmentId, string? lang, string facts, CancellationToken cancellationToken)
    {
        var language = AiNarrative.NormalizeLang(lang);
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");
        var (settings, client) = await aiSettings.ResolveClientAsync(cancellationToken) ?? throw new AiNotConfiguredException();

        var instruction = language == "pt-BR"
            ? "Escreva o resumo executivo (2 a 3 parágrafos, português do Brasil) para a primeira página de um relatório de assessment de modernização .NET, a partir SOMENTE dos fatos abaixo. Público: diretoria e liderança técnica. Diga o estado geral, os 2–3 riscos que mais importam e o caminho recomendado com faixa de esforço. Sem listas, sem jargão, sem inventar números que não estão nos fatos."
            : "Write the executive summary (2–3 paragraphs, English) for page one of a .NET modernization assessment report, from ONLY the facts below. Audience: executives and technical leadership. State the overall condition, the 2–3 risks that matter most, and the recommended path with its effort range. No bullet lists, no jargon, never invent numbers absent from the facts.";

        var reply = await client.CompleteAsync(new ChatRequest(
            "You are a senior modernization consultant writing for executives. Precise, sober, no marketing language.",
            instruction + "\n\nFACTS:\n" + facts, MaxTokens: 900, Temperature: 0.3), cancellationToken);
        var text = reply.Text.Trim();
        if (text.Length == 0)
        {
            throw new ChatProviderException($"{client.Provider} returned an empty summary.");
        }

        var existing = await UpsertAsync(assessment, AiNarrative.Kinds.ExecutiveSummary, "summary", language, text, reply, cancellationToken);
        logger.LogInformation("Executive summary written for {AssessmentId} via {Provider}/{Model} in {Lang}.", assessmentId, settings.Provider, reply.Model, language);
        return new NarrativeResult(text, reply.Model, false, existing.CreatedAtUtc);
    }

    public const string MigrationPlanKey = "plan";

    public async Task<NarrativeResult?> GetMigrationPlanAsync(Guid assessmentId, string? lang, CancellationToken cancellationToken)
    {
        var existing = await narratives.GetAsync(assessmentId, AiNarrative.Kinds.MigrationPlan, MigrationPlanKey, AiNarrative.NormalizeLang(lang), cancellationToken);
        return existing is null ? null : NarrativeResult.FromStored(existing);
    }

    /// <summary>
    /// Drafts the migration plan for the recommended strategy from the fact sheet the
    /// reporting layer assembled (profile, strategy rationale, estimate, roadmap phases
    /// and work items). Markdown out, so the UI, the report and the .md export share it.
    /// No source code is sent; the model may not invent numbers absent from the facts.
    /// </summary>
    public async Task<NarrativeResult> WriteMigrationPlanAsync(Guid assessmentId, string? lang, string facts, CancellationToken cancellationToken)
    {
        var language = AiNarrative.NormalizeLang(lang);
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");
        var (settings, client) = await aiSettings.ResolveClientAsync(cancellationToken) ?? throw new AiNotConfiguredException();

        var reply = await client.CompleteAsync(new ChatRequest(
            "You are a senior .NET modernization consultant drafting an execution plan. Precise, sober, practical; explicit about uncertainty. You only know what the prompt states.",
            PlanInstruction(language) + "\n\nFACTS:\n" + facts, MaxTokens: 4000, Temperature: 0.3), cancellationToken);
        var text = reply.Text.Trim();
        if (text.Length == 0)
        {
            throw new ChatProviderException($"{client.Provider} returned an empty plan.");
        }

        var existing = await UpsertAsync(assessment, AiNarrative.Kinds.MigrationPlan, MigrationPlanKey, language, text, reply, cancellationToken);
        logger.LogInformation("Migration plan drafted for {AssessmentId} via {Provider}/{Model} in {Lang} ({Chars} chars).", assessmentId, settings.Provider, reply.Model, language, text.Length);
        return new NarrativeResult(existing.Text, reply.Model, false, existing.CreatedAtUtc);
    }

    public static string PlanInstruction(string language) => language == "pt-BR"
        ? "Escreva, em português do Brasil e em Markdown, o plano de migração para a estratégia recomendada abaixo, SOMENTE a partir dos fatos fornecidos. Público: liderança técnica e o time que vai executar.\n"
          + "Estrutura obrigatória (títulos '##', nesta ordem):\n"
          + "## Objetivo e escopo — o que muda, o que fica de fora, o resultado esperado.\n"
          + "## Abordagem — por que esta estratégia vence as alternativas (use a justificativa dos fatos) e os princípios de execução (fatias pequenas, sempre entregável, testes de caracterização antes de mexer).\n"
          + "## Fases — uma subseção '###' por fase do roadmap, na ordem dada, com: objetivo, atividades concretas ligadas aos itens de trabalho e quantidades listados, entregáveis, critérios de saída, riscos da fase. Cite as faixas de esforço/duração da fase como estimativas.\n"
          + "## Primeiros 30 dias — ações imediatas de maior retorno (segurança crítica, segredos, pacotes vulneráveis, baseline de testes).\n"
          + "## Time e governança — papéis, cadência de acompanhamento, métricas de progresso (índice de saúde, findings abertos, projetos migrados).\n"
          + "## Riscos e mitigações — lista, incluindo os bloqueadores e a confiança da estimativa.\n"
          + "## Premissas e limites — o que este plano assume e o que não cobre (nada de compromisso de prazo; são faixas).\n"
          + "Regras: não invente números, nomes de arquivos, tecnologias ou pessoas que não estejam nos fatos; use as faixas e quantidades exatamente como dadas; nada de marketing; frases curtas; listas com '-' quando ajudar. Tamanho alvo: 900 a 1400 palavras."
        : "Write, in English and in Markdown, the migration plan for the recommended strategy below, from ONLY the facts provided. Audience: technical leadership and the team that will execute it.\n"
          + "Mandatory structure ('##' headings, in this order):\n"
          + "## Objective and scope — what changes, what stays out, the expected outcome.\n"
          + "## Approach — why this strategy beats the alternatives (use the rationale in the facts) and the execution principles (small slices, always shippable, characterization tests before touching code).\n"
          + "## Phases — one '###' subsection per roadmap phase, in the given order, with: goal, concrete activities tied to the listed work items and quantities, deliverables, exit criteria, phase risks. Quote the phase effort/duration ranges as estimates.\n"
          + "## First 30 days — the highest-return immediate actions (critical security, secrets, vulnerable packages, test baseline).\n"
          + "## Team and governance — roles, review cadence, progress metrics (health score, open findings, projects migrated).\n"
          + "## Risks and mitigations — a list, including the blockers and the estimate's confidence.\n"
          + "## Assumptions and limits — what this plan assumes and what it does not cover (no date commitments; these are ranges).\n"
          + "Rules: never invent numbers, file names, technologies or people absent from the facts; use the ranges and quantities exactly as given; no marketing language; short sentences; '-' lists where they help. Target length: 900 to 1400 words.";

    public const string PrSummaryKeyPrefix = "run:";

    /// <summary>
    /// Two or three sentences for the pull-request reviewer, from the run comparison and
    /// the gate result only — cached per run and language, so CI re-runs do not pay twice.
    /// </summary>
    public async Task<NarrativeResult> SummarizeRunAsync(Guid assessmentId, RunComparison comparison, QualityGateResult gate, string? lang, CancellationToken cancellationToken)
    {
        var language = AiNarrative.NormalizeLang(lang);
        var key = PrSummaryKeyPrefix + comparison.Current.RunId;
        var existing = await narratives.GetAsync(assessmentId, AiNarrative.Kinds.PrSummary, key, language, cancellationToken);
        if (existing is not null)
        {
            return NarrativeResult.FromStored(existing);
        }

        var assessment = await assessments.GetAsync(assessmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");
        var (settings, client) = await aiSettings.ResolveClientAsync(cancellationToken) ?? throw new AiNotConfiguredException();

        var reply = await client.CompleteAsync(new ChatRequest(
            "You are a senior .NET reviewer writing one short note on a pull request. Sober, specific, no marketing; only the facts given.",
            PrSummaryInstruction(language) + "\n\nFACTS:\n" + PrSummaryFacts(assessment.Name, comparison, gate), MaxTokens: 300, Temperature: 0.2), cancellationToken);
        var text = reply.Text.Trim();
        if (text.Length == 0)
        {
            throw new ChatProviderException($"{client.Provider} returned an empty summary.");
        }

        var stored = await UpsertAsync(assessment, AiNarrative.Kinds.PrSummary, key, language, text, reply, cancellationToken);
        logger.LogInformation("PR summary written for {AssessmentId} run #{Run} via {Provider}/{Model}.", assessmentId, comparison.Current.Number, settings.Provider, reply.Model);
        return new NarrativeResult(stored.Text, reply.Model, false, stored.CreatedAtUtc);
    }

    public static string PrSummaryInstruction(string language) => language == "pt-BR"
        ? "Escreva 2 ou 3 frases em português do Brasil para quem revisa o pull request: o que esta execução mudou em relação à anterior, se o gate passou e o que corrigir antes do merge. Texto corrido, sem listas, sem títulos, sem números que não estejam nos fatos."
        : "Write 2 or 3 sentences in English for the pull-request reviewer: what this run changed versus the previous one, whether the gate passed and what to fix before merging. Plain prose, no lists, no headings, no numbers absent from the facts.";

    public static string PrSummaryFacts(string assessmentName, RunComparison c, QualityGateResult gate)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Assessment: {assessmentName}. Run #{c.Current.Number}; health {c.Current.HealthScore?.ToString() ?? "n/a"}/100" + (c.Previous is null ? " (first run, no baseline)." : $"; previous run #{c.Previous.Number} scored {c.Previous.HealthScore?.ToString() ?? "n/a"} (delta {(c.HealthDelta is { } d ? (d > 0 ? "+" : "") + d : "n/a")})."));
        sb.AppendLine($"Gate: {(gate.Evaluated ? (gate.Passed ? "passed" : "failed") : "not evaluated")}" + (gate.FailOn is null && gate.MinScore is null ? " (no rules configured)." : $" (fail on {gate.FailOn ?? "-"}, min score {gate.MinScore?.ToString() ?? "-"})."));
        foreach (var v in gate.Violations)
        {
            sb.AppendLine($"Violation: {v}");
        }

        sb.AppendLine($"Open by severity: {string.Join(", ", gate.OpenBySeverity.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))}.");
        sb.AppendLine($"Since previous run: {c.New.Sum(r => r.Count)} new, {c.Resolved.Sum(r => r.Count)} resolved, {c.Regressed.Sum(r => r.Count)} regressed.");
        foreach (var r in c.New.OrderByDescending(r => r.MaxSeverity).ThenByDescending(r => r.Count).Take(8))
        {
            sb.AppendLine($"New: [{r.MaxSeverity}] {r.Title} ×{r.Count}{(r.SampleLocations.Count > 0 ? $" at {r.SampleLocations[0]}" : "")}");
        }

        foreach (var r in c.Regressed.Take(5))
        {
            sb.AppendLine($"Regressed: [{r.MaxSeverity}] {r.Title} ×{r.Count}");
        }

        foreach (var r in c.Resolved.Take(5))
        {
            sb.AppendLine($"Resolved: {r.Title} ×{r.Count}");
        }

        return sb.ToString();
    }

    private async Task<AiNarrative> UpsertAsync(Assessment assessment, string kind, string key, string language, string text, ChatResult reply, CancellationToken cancellationToken)
    {
        var existing = await narratives.GetAsync(assessment.Id, kind, key, language, cancellationToken);
        if (existing is null)
        {
            existing = new AiNarrative(Guid.NewGuid(), assessment.TenantId, assessment.Id, kind, key, language, text, reply.Model, reply.InputTokens, reply.OutputTokens);
            narratives.Add(existing);
        }
        else
        {
            existing.Replace(text, reply.Model, reply.InputTokens, reply.OutputTokens);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return existing;
    }
}
