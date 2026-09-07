using System.Globalization;
using System.Text;
using Atlas.Domain.Findings;

namespace Atlas.Application.Assessments;

/// <summary>Everything the pull-request comment says, gathered by the API; rendering is pure so it can be tested and versioned.</summary>
public sealed record PrCommentInput(
    string AssessmentName,
    Guid AssessmentId,
    RunComparison? Comparison,
    QualityGateResult Gate,
    string? PublicBaseUrl,
    string Version,
    string? Lang,
    string? AiSummary = null,
    string? AiModel = null);

/// <summary>
/// The Markdown Atlas posts on a pull request: gate verdict first, health and
/// its delta, what the run changed, the new findings a reviewer should look at,
/// the gate's reasons, an optional AI paragraph (always labelled) and links.
/// A hidden marker lets CI find and update its own comment instead of stacking.
/// </summary>
public static class PrComment
{
    public const string Marker = "<!-- atlas-pr-comment -->";
    public const int MaxRows = 10;

    public static string Render(PrCommentInput i)
    {
        var pt = i.Lang is not null && i.Lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var c = pt ? CultureInfo.GetCultureInfo("pt-BR") : CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var cur = i.Comparison?.Current;
        var prev = i.Comparison?.Previous;
        var gate = i.Gate;
        var hasGateRules = gate.FailOn is not null || gate.MinScore is not null || gate.FailOnNew is not null;

        var verdict = !gate.Evaluated
            ? (pt ? "⏳ sem execução concluída" : "⏳ no completed run")
            : !hasGateRules
                ? (pt ? "ℹ️ sem gate configurado" : "ℹ️ no gate configured")
                : gate.Passed
                    ? (pt ? "✅ gate aprovado" : "✅ gate passed")
                    : (pt ? "❌ gate reprovado" : "❌ gate failed");

        sb.AppendLine(Marker);
        sb.AppendLine($"## ◈ Atlas · {Escape(i.AssessmentName)} — {verdict}");
        sb.AppendLine();

        if (cur is null)
        {
            sb.AppendLine(pt
                ? "Ainda não há execução concluída para este assessment; o gate não pôde ser avaliado."
                : "There is no completed run for this assessment yet, so the gate could not be evaluated.");
        }
        else
        {
            var score = cur.HealthScore?.ToString(c) ?? "—";
            var delta = i.Comparison!.HealthDelta;
            var deltaText = delta is null || prev is null ? "" : delta == 0
                ? (pt ? $" (= vs #{prev.Number})" : $" (= vs run #{prev.Number})")
                : $" ({(delta > 0 ? "▲ +" : "▼ ")}{delta} {(pt ? "vs" : "vs run")} #{prev.Number})";
            sb.AppendLine($"**{(pt ? "Saúde" : "Health")} {score}/100**{deltaText} · {(pt ? "risco" : "risk")} **{Risk(cur.HealthScore, pt)}** · {(pt ? "execução" : "run")} #{cur.Number}{(cur.CommitSha is { Length: > 0 } sha ? $" `{sha[..Math.Min(10, sha.Length)]}`" : "")}");
            sb.AppendLine();

            var open = gate.OpenBySeverity;
            sb.AppendLine($"| {(pt ? "Abertos" : "Open")} | 🔴 Critical | 🟠 High | 🟡 Medium | 🔵 Low | ⚪ Info |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            sb.AppendLine($"| **{open.Values.Sum().ToString("N0", c)}** | {Count(open, Severity.Critical, c)} | {Count(open, Severity.High, c)} | {Count(open, Severity.Medium, c)} | {Count(open, Severity.Low, c)} | {Count(open, Severity.Informational, c)} |");
            sb.AppendLine();

            if (prev is null)
            {
                sb.AppendLine(pt ? "_Primeira execução — sem baseline para comparar._" : "_First run — no baseline to compare against._");
            }
            else
            {
                var newCount = i.Comparison.New.Sum(r => r.Count);
                var resolved = i.Comparison.Resolved.Sum(r => r.Count);
                var regressed = i.Comparison.Regressed.Sum(r => r.Count);
                sb.AppendLine(pt
                    ? $"Desde a execução #{prev.Number}: **{newCount} novo(s)**, {resolved} resolvido(s), {regressed} regredido(s)."
                    : $"Since run #{prev.Number}: **{newCount} new**, {resolved} resolved, {regressed} regressed.");
                if (i.Comparison.SameCommit)
                {
                    sb.AppendLine(pt ? "_Mesmo commit da execução anterior — diferenças vêm de regras ou dados, não do código._" : "_Same commit as the previous run — differences come from rules or data, not from the code._");
                }
            }

            sb.AppendLine();
            AppendRules(sb, pt ? "🆕 Novos nesta execução" : "🆕 New in this run", i.Comparison.New, pt, c);
            AppendRules(sb, pt ? "↩️ Regredidos" : "↩️ Regressed", i.Comparison.Regressed, pt, c);
        }

        if (hasGateRules && gate.Evaluated)
        {
            sb.AppendLine($"### {(pt ? "Gate" : "Gate")}");
            var rules = new List<string>();
            if (gate.FailOn is not null)
            {
                rules.Add(pt ? $"falha com findings abertos ≥ {gate.FailOn}" : $"fail on open findings ≥ {gate.FailOn}");
            }

            if (gate.MinScore is not null)
            {
                rules.Add(pt ? $"saúde mínima {gate.MinScore}" : $"minimum health {gate.MinScore}");
            }

            if (gate.FailOnNew is not null)
            {
                rules.Add(pt ? $"falha com findings NOVOS ≥ {gate.FailOnNew}" : $"fail on NEW findings ≥ {gate.FailOnNew}");
            }

            sb.AppendLine($"_{string.Join(" · ", rules)}_");
            sb.AppendLine();
            if (gate.Passed)
            {
                sb.AppendLine(pt ? "- ✓ Nenhuma violação." : "- ✓ No violations.");
            }
            else
            {
                foreach (var v in gate.Violations)
                {
                    sb.AppendLine($"- ✗ {Escape(v)}");
                }
            }

            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(i.AiSummary))
        {
            sb.AppendLine($"> 🤖 {Escape(i.AiSummary.Trim())}");
            sb.AppendLine($"> <sub>{(pt ? "Escrito por IA" : "Written by AI")}{(i.AiModel is null ? "" : $" ({i.AiModel})")} {(pt ? "a partir dos números acima; revise antes de agir." : "from the figures above; review before acting.")}</sub>");
            sb.AppendLine();
        }

        var links = new List<string>();
        if (!string.IsNullOrWhiteSpace(i.PublicBaseUrl))
        {
            var b = i.PublicBaseUrl.TrimEnd('/');
            links.Add($"[{(pt ? "Abrir assessment" : "Open assessment")}]({b}/assessments/{i.AssessmentId})");
            links.Add($"[{(pt ? "Relatório" : "Report")}]({b}/api/assessments/{i.AssessmentId}/report?lang={(pt ? "pt-BR" : "en")})");
            links.Add($"[Findings]({b}/assessments/{i.AssessmentId}?tab=findings)");
        }

        links.Add($"Atlas {i.Version}");
        sb.AppendLine($"<sub>{string.Join(" · ", links)}</sub>");
        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendRules(StringBuilder sb, string title, IReadOnlyList<RuleDelta> rules, bool pt, CultureInfo c)
    {
        if (rules.Count == 0)
        {
            return;
        }

        var ordered = rules.OrderByDescending(r => Rank(r.MaxSeverity)).ThenByDescending(r => r.Count).ToList();
        var total = rules.Sum(r => r.Count);
        sb.AppendLine($"### {title} ({total.ToString("N0", c)})");
        sb.AppendLine($"| | {(pt ? "Finding" : "Finding")} | {(pt ? "Onde" : "Where")} |");
        sb.AppendLine("|---|---|---|");
        foreach (var r in ordered.Take(MaxRows))
        {
            var where = r.SampleLocations.Count == 0 ? "—" : $"`{Escape(r.SampleLocations[0])}`{(r.SampleLocations.Count > 1 ? $" +{r.SampleLocations.Count - 1}" : "")}";
            sb.AppendLine($"| {Badge(r.MaxSeverity)} | {Escape(r.Title)}{(r.Count > 1 ? $" ×{r.Count.ToString("N0", c)}" : "")} <br><sub>`{r.RuleId}` · {r.Category}</sub> | {where} |");
        }

        if (ordered.Count > MaxRows)
        {
            sb.AppendLine();
            sb.AppendLine(pt ? $"_… e mais {ordered.Count - MaxRows} regra(s)._" : $"_… and {ordered.Count - MaxRows} more rule(s)._");
        }

        sb.AppendLine();
    }

    private static string Count(IReadOnlyDictionary<Severity, int> open, Severity s, CultureInfo c) => open.TryGetValue(s, out var n) ? n.ToString("N0", c) : "0";

    private static int Rank(Severity s) => s switch { Severity.Critical => 4, Severity.High => 3, Severity.Medium => 2, Severity.Low => 1, _ => 0 };

    private static string Badge(Severity s) => s switch
    {
        Severity.Critical => "🔴 Critical",
        Severity.High => "🟠 High",
        Severity.Medium => "🟡 Medium",
        Severity.Low => "🔵 Low",
        _ => "⚪ Info",
    };

    private static string Risk(int? score, bool pt) => score switch
    {
        null => "—",
        < 40 => pt ? "Crítico" : "Critical",
        < 60 => pt ? "Alto" : "High",
        < 80 => pt ? "Médio" : "Medium",
        _ => pt ? "Baixo" : "Low",
    };

    /// <summary>Keeps user-controlled text inside a Markdown table cell and out of the markup.</summary>
    public static string Escape(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Replace("<", "&lt;").Replace(">", "&gt;");
}
