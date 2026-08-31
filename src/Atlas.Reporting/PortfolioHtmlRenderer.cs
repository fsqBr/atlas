using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Atlas.Application.Portfolio;
using Atlas.Domain.Findings;

namespace Atlas.Reporting;

/// <summary>
/// Renders the portfolio executive report: self-contained HTML (inline CSS, inline SVG trend),
/// printable to PDF by the same renderers as the per-assessment report. White-label options
/// (brand, logo, accent) apply the same way.
/// </summary>
public static class PortfolioHtmlRenderer
{
    private const int MaxTrendRows = 12;
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    public static string Render(PortfolioReport report, ReportLocale? locale = null, ReportOptions? options = null)
    {
        var l = locale ?? ReportLocale.En;
        var s = Strings(l);
        var c = l.Culture;
        var m = report.Summary;
        var sb = new StringBuilder(32 * 1024);

        sb.Append("<!DOCTYPE html><html lang=\"").Append(E(l.Code)).Append("\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(E(report.BrandName)).Append(" — ").Append(E(s["title"])).Append("</title>")
          .Append("<style>").Append(Css);
        if (options?.AccentColor is { } accent && System.Text.RegularExpressions.Regex.IsMatch(accent, "^#[0-9A-Fa-f]{6}$"))
        {
            sb.Append(":root{--accent:").Append(accent).Append('}');
        }

        sb.Append("</style></head><body><main class=\"page\">");

        // Header
        sb.Append("<header class=\"head\">");
        if (options?.LogoDataUri is { } logo && logo.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !logo.Contains('<'))
        {
            sb.Append("<img class=\"logo\" alt=\"\" src=\"").Append(E(logo)).Append("\">");
        }

        sb.Append("<p class=\"eyebrow\">").Append(E(report.BrandName)).Append(" · ").Append(E(s["eyebrow"])).Append("</p>")
          .Append("<h1>").Append(E(s["title"])).Append("</h1>")
          .Append("<dl class=\"meta\">")
          .Append("<dt>").Append(E(s["scope"])).Append("</dt><dd>")
          .Append(report.Tag is null ? E(s["wholeEstate"]) : E(s["productGroup"]) + ": " + E(report.Tag)).Append("</dd>")
          .Append("<dt>").Append(E(s["generated"])).Append("</dt><dd>").Append(report.GeneratedAtUtc.ToString("g", c)).Append(" UTC</dd>");
        if (report.PreparedBy is not null)
        {
            sb.Append("<dt>").Append(E(s["preparedBy"])).Append("</dt><dd>").Append(E(report.PreparedBy)).Append("</dd>");
        }

        sb.Append("</dl></header>");

        // KPI tiles
        sb.Append("<section class=\"tiles\">");
        Tile(sb, m.Assessed.ToString(c) + " / " + m.Assessments.ToString(c), s["assessments"]);
        Tile(sb, m.AverageScore is { } avg ? avg.ToString("0.#", c) : "—", s["avgScore"]);
        Tile(sb, m.OpenFindings.ToString("N0", c), s["openFindings"]);
        Tile(sb, m.Lines.ToString("N0", c), s["linesOfCode"]);
        Tile(sb, m.Projects.ToString(c), s["projects"]);
        Tile(sb, m.LegacyProjects.ToString(c), s["legacyProjects"]);
        sb.Append("</section>");

        // Risk distribution + severity split + targets
        sb.Append("<section><h2>").Append(E(s["riskDistribution"])).Append("</h2><table><thead><tr>");
        foreach (var risk in m.ByRisk.Keys.OrderBy(k => (int)k))
        {
            sb.Append("<th>").Append(E(Local(s, risk.ToString()))).Append("</th>");
        }

        sb.Append("<th>").Append(E(s["openBySeverity"])).Append("</th></tr></thead><tbody><tr>");
        foreach (var risk in m.ByRisk.Keys.OrderBy(k => (int)k))
        {
            sb.Append("<td class=\"num\">").Append(m.ByRisk[risk].ToString(c)).Append("</td>");
        }

        sb.Append("<td>").Append(E(string.Join(" · ", m.OpenBySeverity.Where(kv => kv.Value > 0).OrderByDescending(kv => (int)kv.Key)
            .Select(kv => $"{Local(s, kv.Key.ToString())} {kv.Value.ToString("N0", c)}")))).Append("</td>");
        sb.Append("</tr></tbody></table>");
        if (m.Targets is { } targets && targets.Any(kv => kv.Value > 0 && kv.Key.ToString() != "None"))
        {
            sb.Append("<p class=\"muted\">").Append(E(s["targets"])).Append(": ")
              .Append(E(string.Join(" · ", targets.Where(kv => kv.Value > 0 && kv.Key.ToString() != "None")
                  .Select(kv => $"{Local(s, kv.Key.ToString())} {kv.Value.ToString(c)}")))).Append("</p>");
        }

        sb.Append("</section>");

        RenderTrend(sb, report.Trend, s, c);
        RenderTopRules(sb, m.TopRules, s, c);
        RenderRows(sb, m.Rows, s, c);

        sb.Append("<footer class=\"note\"><p>").Append(E(s["note"])).Append("</p></footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    /// <summary>Chromium print footer: brand, scope, confidentiality and page X of Y.</summary>
    public static string RenderPdfFooter(PortfolioReport report, ReportLocale? locale = null)
    {
        var l = locale ?? ReportLocale.En;
        var s = Strings(l);
        var scope = report.Tag is null ? s["title"] : $"{s["title"]} · {report.Tag}";
        return "<html><head><style>body{margin:0;font:9px ui-sans-serif,system-ui,sans-serif;color:#55616A}.f{display:flex;justify-content:space-between;width:100%;box-sizing:border-box;padding:0 0.5in}</style></head><body>"
            + "<div class=\"f\"><span>" + E(report.BrandName) + " · " + E(scope) + " · " + E(l.Confidential) + "</span>"
            + "<span>" + E(l.PageOf).Replace("{0}", "<span class=\"pageNumber\"></span>").Replace("{1}", "<span class=\"totalPages\"></span>") + "</span></div></body></html>";
    }

    private static void RenderTrend(StringBuilder sb, IReadOnlyList<PortfolioTrendPoint> trend, IReadOnlyDictionary<string, string> s, CultureInfo c)
    {
        sb.Append("<section><h2>").Append(E(s["trend"])).Append("</h2>");
        var scored = trend.Where(p => p.AverageScore is not null).ToList();
        if (scored.Count < 2)
        {
            sb.Append("<p class=\"muted\">").Append(E(s["noTrend"])).Append("</p></section>");
            return;
        }

        // Inline SVG polyline of the average score (0-100), newest at the right.
        const int W = 640, H = 120, Pad = 6;
        var step = (W - 2 * Pad) / (double)(scored.Count - 1);
        var points = string.Join(" ", scored.Select((p, i) =>
        {
            var x = Pad + i * step;
            var y = Pad + (H - 2 * Pad) * (1 - Math.Clamp(p.AverageScore!.Value, 0, 100) / 100.0);
            return x.ToString("0.#", CultureInfo.InvariantCulture) + "," + y.ToString("0.#", CultureInfo.InvariantCulture);
        }));
        sb.Append("<svg class=\"trend\" viewBox=\"0 0 ").Append(W).Append(' ').Append(H).Append("\" role=\"img\">")
          .Append("<polyline fill=\"none\" stroke=\"var(--accent,#1F6E68)\" stroke-width=\"2\" points=\"").Append(points).Append("\"/></svg>");

        sb.Append("<table><thead><tr><th>").Append(E(s["week"])).Append("</th><th class=\"num\">").Append(E(s["avgScore"]))
          .Append("</th><th class=\"num\">").Append(E(s["openFindings"])).Append("</th><th class=\"num\">").Append(E(s["assessed"])).Append("</th></tr></thead><tbody>");
        foreach (var p in trend.TakeLast(MaxTrendRows))
        {
            sb.Append("<tr><td>").Append(p.Date.ToString("d", c)).Append("</td><td class=\"num\">")
              .Append(p.AverageScore is { } a ? a.ToString("0.#", c) : "—").Append("</td><td class=\"num\">")
              .Append(p.OpenFindings.ToString("N0", c)).Append("</td><td class=\"num\">")
              .Append(p.Assessed.ToString(c)).Append("</td></tr>");
        }

        sb.Append("</tbody></table></section>");
    }

    private static void RenderTopRules(StringBuilder sb, IReadOnlyList<PortfolioRule> rules, IReadOnlyDictionary<string, string> s, CultureInfo c)
    {
        sb.Append("<section><h2>").Append(E(s["topRules"])).Append("</h2>");
        if (rules.Count == 0)
        {
            sb.Append("<p class=\"muted\">").Append(E(s["none"])).Append("</p></section>");
            return;
        }

        sb.Append("<table><thead><tr><th>").Append(E(s["rule"])).Append("</th><th>").Append(E(s["category"]))
          .Append("</th><th>").Append(E(s["severity"])).Append("</th><th class=\"num\">").Append(E(s["openFindings"]))
          .Append("</th><th class=\"num\">").Append(E(s["assessments"])).Append("</th></tr></thead><tbody>");
        foreach (var rule in rules)
        {
            sb.Append("<tr><td>").Append(E(rule.Title)).Append(" <span class=\"mono muted\">").Append(E(rule.RuleId)).Append("</span></td><td>")
              .Append(E(Local(s, rule.Category.ToString()))).Append("</td><td>").Append(E(Local(s, rule.MaxSeverity.ToString()))).Append("</td><td class=\"num\">")
              .Append(rule.Count.ToString("N0", c)).Append("</td><td class=\"num\">").Append(rule.Assessments.ToString(c)).Append("</td></tr>");
        }

        sb.Append("</tbody></table></section>");
    }

    private static void RenderRows(StringBuilder sb, IReadOnlyList<PortfolioRow> rows, IReadOnlyDictionary<string, string> s, CultureInfo c)
    {
        sb.Append("<section><h2>").Append(E(s["assessmentsTable"])).Append("</h2>")
          .Append("<table><thead><tr><th>").Append(E(s["name"])).Append("</th><th class=\"num\">").Append(E(s["score"]))
          .Append("</th><th>").Append(E(s["risk"])).Append("</th><th class=\"num\">").Append(E(s["openFindings"]))
          .Append("</th><th class=\"num\">").Append(E(s["lines"])).Append("</th><th class=\"num\">").Append(E(s["projects"]))
          .Append("</th><th class=\"num\">").Append(E(s["legacy"])).Append("</th><th>").Append(E(s["tags"])).Append("</th></tr></thead><tbody>");
        foreach (var row in rows.OrderBy(r => r.Score ?? int.MaxValue))
        {
            sb.Append("<tr><td>").Append(E(row.Name)).Append("</td><td class=\"num\">").Append(row.Score?.ToString(c) ?? "—")
              .Append("</td><td>").Append(E(row.Risk is { } risk ? Local(s, risk.ToString()) : "—")).Append("</td><td class=\"num\">")
              .Append(row.OpenFindings?.ToString("N0", c) ?? "—").Append("</td><td class=\"num\">").Append(row.Lines.ToString("N0", c))
              .Append("</td><td class=\"num\">").Append(row.Projects.ToString(c)).Append("</td><td class=\"num\">").Append(row.LegacyProjects.ToString(c))
              .Append("</td><td>").Append(E(row.Tags is { Count: > 0 } tags ? string.Join(", ", tags) : "—")).Append("</td></tr>");
        }

        sb.Append("</tbody></table></section>");
    }

    private static void Tile(StringBuilder sb, string value, string label) =>
        sb.Append("<div class=\"tile\"><span class=\"tile-v\">").Append(E(value)).Append("</span><span class=\"tile-l\">").Append(E(label)).Append("</span></div>");

    private static string Local(IReadOnlyDictionary<string, string> s, string key) => s.TryGetValue(key, out var v) ? v : key;

    private static string E(string? value) => Encoder.Encode(value ?? string.Empty);

    private static IReadOnlyDictionary<string, string> Strings(ReportLocale l) =>
        l.Code.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? Pt : En;

    private static readonly Dictionary<string, string> En = new()
    {
        ["title"] = "Portfolio Report",
        ["eyebrow"] = "Software Portfolio",
        ["scope"] = "Scope",
        ["wholeEstate"] = "Entire portfolio",
        ["productGroup"] = "Product group",
        ["generated"] = "Generated",
        ["preparedBy"] = "Prepared by",
        ["assessments"] = "Assessed / total",
        ["avgScore"] = "Average health",
        ["openFindings"] = "Open findings",
        ["linesOfCode"] = "Lines of code",
        ["projects"] = "Projects",
        ["legacyProjects"] = "Legacy projects",
        ["riskDistribution"] = "Risk distribution",
        ["openBySeverity"] = "Open by severity",
        ["targets"] = "Targets",
        ["trend"] = "Health trend (weekly)",
        ["noTrend"] = "Not enough completed runs yet to draw a trend — it appears after two or more weeks of history.",
        ["week"] = "Week",
        ["assessed"] = "Assessed",
        ["topRules"] = "Top rules across the estate",
        ["rule"] = "Rule",
        ["category"] = "Category",
        ["severity"] = "Max severity",
        ["none"] = "none",
        ["assessmentsTable"] = "Assessments",
        ["name"] = "Name",
        ["score"] = "Score",
        ["risk"] = "Risk",
        ["lines"] = "Lines",
        ["legacy"] = "Legacy",
        ["tags"] = "Tags",
        ["note"] = "Deterministic snapshot computed from persisted assessment runs — no code was re-analyzed to produce this document. Per-assessment detail, evidence and remediation live in each assessment's own report.",
    };

    private static readonly Dictionary<string, string> Pt = new()
    {
        ["title"] = "Relatório de Portfólio",
        ["eyebrow"] = "Portfólio de Software",
        ["scope"] = "Escopo",
        ["wholeEstate"] = "Portfólio inteiro",
        ["productGroup"] = "Grupo de produto",
        ["generated"] = "Gerado",
        ["preparedBy"] = "Preparado por",
        ["assessments"] = "Avaliados / total",
        ["avgScore"] = "Saúde média",
        ["openFindings"] = "Findings abertos",
        ["linesOfCode"] = "Linhas de código",
        ["projects"] = "Projetos",
        ["legacyProjects"] = "Projetos legados",
        ["riskDistribution"] = "Distribuição de risco",
        ["openBySeverity"] = "Abertos por severidade",
        ["targets"] = "Metas",
        ["trend"] = "Tendência de saúde (semanal)",
        ["noTrend"] = "Ainda não há runs suficientes para desenhar a tendência — ela aparece com duas ou mais semanas de histórico.",
        ["week"] = "Semana",
        ["assessed"] = "Avaliados",
        ["topRules"] = "Principais regras no portfólio",
        ["rule"] = "Regra",
        ["category"] = "Categoria",
        ["severity"] = "Severidade máx.",
        ["none"] = "nenhuma",
        ["assessmentsTable"] = "Assessments",
        ["name"] = "Nome",
        ["score"] = "Score",
        ["risk"] = "Risco",
        ["lines"] = "Linhas",
        ["legacy"] = "Legados",
        ["tags"] = "Tags",
        ["note"] = "Retrato determinístico calculado a partir dos runs persistidos — nenhum código foi reanalisado para produzir este documento. O detalhe por assessment, com evidências e remediação, está no relatório de cada assessment.",
        // shared enum labels
        ["Low"] = "Baixo",
        ["Medium"] = "Médio",
        ["High"] = "Alto",
        ["Critical"] = "Crítico",
        ["Informational"] = "Informativo",
        ["Security"] = "Segurança",
        ["Secrets"] = "Segredos",
        ["Privacy"] = "Privacidade",
        ["Quality"] = "Qualidade",
        ["Architecture"] = "Arquitetura",
        ["Dependencies"] = "Dependências",
        ["Modernization"] = "Modernização",
        ["Database"] = "Banco de dados",
        ["Infrastructure"] = "Infraestrutura",
        ["JavaScript"] = "JavaScript",
        ["OnTrack"] = "No rumo",
        ["AtRisk"] = "Em risco",
        ["Achieved"] = "Alcançada",
        ["Missed"] = "Perdida",
    };

    private const string Css = """
        :root{--accent:#1F6E68;--ink:#1C2528;--soft:#55616A;--line:#E3E8EA;--bg:#FFFFFF}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.45 ui-sans-serif,system-ui,'Segoe UI',sans-serif}
        .page{max-width:960px;margin:0 auto;padding:32px 40px}
        .head{border-bottom:3px solid var(--accent);padding-bottom:14px;margin-bottom:20px}
        .logo{max-height:44px;margin-bottom:8px}
        .eyebrow{margin:0;font-size:.78rem;letter-spacing:.12em;text-transform:uppercase;color:var(--soft)}
        h1{margin:.15em 0 .3em;font-size:1.7rem}h2{margin:1.4em 0 .4em;font-size:1.12rem;color:var(--accent)}
        .meta{display:grid;grid-template-columns:auto 1fr;gap:2px 14px;margin:0;font-size:.86rem}
        .meta dt{color:var(--soft)}.meta dd{margin:0}
        .tiles{display:flex;flex-wrap:wrap;gap:10px;margin:16px 0}
        .tile{flex:1 1 130px;border:1px solid var(--line);border-radius:8px;padding:10px 12px;display:flex;flex-direction:column}
        .tile-v{font-size:1.35rem;font-weight:600}.tile-l{font-size:.78rem;color:var(--soft)}
        table{width:100%;border-collapse:collapse;font-size:.86rem;margin:8px 0}
        th{text-align:left;color:var(--soft);font-weight:600;border-bottom:2px solid var(--line);padding:5px 8px}
        td{border-bottom:1px solid var(--line);padding:5px 8px;vertical-align:top}
        th.num,td.num{text-align:right}
        .mono{font-family:ui-monospace,Consolas,monospace;font-size:.78rem}.muted{color:var(--soft)}
        .trend{width:100%;height:auto;border:1px solid var(--line);border-radius:8px;margin:6px 0}
        .note{margin-top:26px;border-top:1px solid var(--line);padding-top:10px;color:var(--soft);font-size:.8rem}
        section{break-inside:avoid}
        @media print{.page{padding:0}}
        """;
}
