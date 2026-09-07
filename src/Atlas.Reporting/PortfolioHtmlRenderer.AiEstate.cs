using System.Globalization;
using System.Text;
using Atlas.Application.AiEstate;

namespace Atlas.Reporting;

/// <summary>The AI Estate section of the portfolio report (governance module V0.5).</summary>
public static partial class PortfolioHtmlRenderer
{
    private static void Tile(StringBuilder sb, string valueHtml, string label, bool raw) =>
        sb.Append("<div class=\"tile\"><span class=\"tile-v\">").Append(raw ? valueHtml : E(valueHtml)).Append("</span><span class=\"tile-l\">").Append(E(label)).Append("</span></div>");

    private static void RenderAiEstate(StringBuilder sb, PortfolioAiEstate? ai, IReadOnlyDictionary<string, string> s, CultureInfo c)
    {
        sb.Append("<section><h2>").Append(E(s["aiEstate"])).Append("</h2>");
        if (ai is null || (ai.AssessmentsWithAi == 0 && ai.Costs is null))
        {
            sb.Append("<p class=\"muted\">").Append(E(s["aiNone"])).Append("</p></section>");
            return;
        }

        if (ai.AssessmentsWithAi == 0)
        {
            RenderCosts(sb, ai.Costs!, s, c);
            sb.Append("<p class=\"muted\">").Append(E(s["aiNone"])).Append("</p></section>");
            return;
        }

        sb.Append("<div class=\"tiles\">");
        Tile(sb, ai.AssessmentsWithAi.ToString(c) + " <span class=\"muted\">" + E(string.Format(c, s["aiScanned"], ai.AssessmentsScanned)) + "</span>", s["aiWithAi"], raw: true);
        Tile(sb, ai.Providers.Count.ToString(c), s["aiProviders"]);
        Tile(sb, ai.McpServers.ToString(c) + (ai.RemoteMcpServers > 0 ? $" <span class=\"muted\">({ai.RemoteMcpServers} {E(s["aiRemoteMcp"])})</span>" : string.Empty), s["aiMcp"], raw: true);
        if (ai.AllowlistConfigured)
        {
            Tile(sb, ai.Unapproved.Sum(u => u.Assessments).ToString(c), s["aiUnapproved"]);
        }

        Tile(sb, ai.RetiredModelReferences.ToString(c), s["aiRetired"]);
        Tile(sb, ai.AiWithPii.ToString(c), s["aiWithPii"]);
        Tile(sb, ai.AiWithSecrets.ToString(c), s["aiWithSecrets"]);
        if (ai.SecretsInMcp > 0)
        {
            Tile(sb, ai.SecretsInMcp.ToString(c), s["aiSecretsMcp"]);
        }

        sb.Append("</div>");

        // Providers across the estate.
        sb.Append("<table><thead><tr><th>").Append(E(s["aiProvider"])).Append("</th><th>").Append(E(s["aiKind"])).Append("</th><th class=\"num\">").Append(E(s["aiAssessments"])).Append("</th><th>").Append(E(s["aiStatus"])).Append("</th></tr></thead><tbody>");
        foreach (var p in ai.Providers)
        {
            var status = !ai.AllowlistConfigured ? s["aiNoAllowlist"] : p.Approved == false ? s["aiNotApproved"] : s["aiApproved"];
            sb.Append("<tr><td><strong>").Append(E(p.Name)).Append("</strong></td><td>").Append(E(Local(s, p.Kind))).Append("</td><td class=\"num\">").Append(p.Assessments.ToString(c)).Append("</td><td")
              .Append(ai.AllowlistConfigured && p.Approved == false ? " style=\"color:#B42318;font-weight:600\"" : string.Empty).Append('>').Append(E(status)).Append("</td></tr>");
        }

        sb.Append("</tbody></table>");

        if (ai.Frameworks.Count > 0)
        {
            sb.Append("<p>").Append(E(s["aiFrameworks"])).Append(": ")
              .Append(E(string.Join(" · ", ai.Frameworks.Select(f => $"{f.Framework} ({f.Count})")))).Append("</p>");
        }

        // Per-assessment rows: where the flags are.
        sb.Append("<table><thead><tr><th>").Append(E(s["aiRowName"])).Append("</th><th>").Append(E(s["aiRowProviders"])).Append("</th><th>").Append(E(s["aiRowFrameworks"]))
          .Append("</th><th class=\"num\">").Append(E(s["aiRowMcp"])).Append("</th><th>").Append(E(s["aiRowFlags"])).Append("</th></tr></thead><tbody>");
        foreach (var row in ai.Rows)
        {
            var flags = new List<string>();
            if (row.Unapproved > 0)
            {
                flags.Add(s["aiFlagUnapproved"]);
            }

            if (row.PiiCoLocated)
            {
                flags.Add(s["aiFlagPii"]);
            }

            if (row.SecretsCoLocated)
            {
                flags.Add(s["aiFlagSecrets"]);
            }

            if (row.RetiredModels > 0)
            {
                flags.Add(s["aiFlagRetired"]);
            }

            if (row.SecretsInMcp > 0)
            {
                flags.Add(s["aiFlagSecretMcp"]);
            }

            sb.Append("<tr><td><strong>").Append(E(row.Name)).Append("</strong>");
            if (row.Tags is { Count: > 0 })
            {
                sb.Append("<br><span class=\"small muted\">").Append(E(string.Join(", ", row.Tags))).Append("</span>");
            }

            sb.Append("</td><td class=\"small\">").Append(E(string.Join(", ", row.Providers))).Append("</td><td class=\"small\">").Append(E(string.Join(", ", row.Frameworks)))
              .Append("</td><td class=\"num\">").Append(row.McpServers.ToString(c)).Append("</td><td class=\"small\">").Append(E(flags.Count == 0 ? "—" : string.Join(" · ", flags))).Append("</td></tr>");
        }

        sb.Append("</tbody></table>");
        if (ai.Costs is not null)
        {
            RenderCosts(sb, ai.Costs, s, c);
        }

        sb.Append("<p class=\"muted\"></p><p class=\"muted\">").Append(E(string.Format(c, s["aiNote"], ai.CatalogVersion ?? "—"))).Append("</p></section>");
    }

    /// <summary>Spend by provider and basis (never summed across bases), seats and activity — from persisted cost facts only.</summary>
    private static void RenderCosts(StringBuilder sb, AiCostSummary costs, IReadOnlyDictionary<string, string> s, CultureInfo c)
    {
        sb.Append("<h3>").Append(E(string.Format(c, s["aiCostTitle"], costs.Days))).Append("</h3>");
        if (costs.Providers.Count > 0)
        {
            sb.Append("<table><thead><tr><th>").Append(E(s["aiProvider"])).Append("</th><th>").Append(E(s["aiCostBasis"])).Append("</th><th class=\"num\">").Append(E(s["aiCostTotal"]))
              .Append("</th><th>").Append(E(s["aiCostTop"])).Append("</th><th>").Append(E(s["aiLastSync"])).Append("</th></tr></thead><tbody>");
            foreach (var p in costs.Providers)
            {
                sb.Append("<tr><td><strong>").Append(E(p.Provider)).Append("</strong></td><td>").Append(E(Local(s, p.Basis))).Append("</td>")
                  .Append("<td class=\"num\">").Append(E(p.Total.ToString("N2", c))).Append(' ').Append(E(p.Currency)).Append("</td>")
                  .Append("<td class=\"small\">").Append(E(string.Join(" · ", p.TopDimensions.Take(4).Select(d => $"{d.Key} {d.Amount.ToString("N2", c)}")))).Append("</td>")
                  .Append("<td class=\"small").Append(p.LastSyncStatus == "Failed" ? " bad" : string.Empty).Append("\">")
                  .Append(E(p.LastSyncAtUtc is { } t ? t.ToString("g", c) + " UTC" : "—")).Append(p.LastSyncError is null ? string.Empty : " · " + E(p.LastSyncError)).Append("</td></tr>");
            }

            sb.Append("</tbody></table>");
        }

        foreach (var seat in costs.Seats)
        {
            sb.Append("<p>").Append(E(string.Format(c, s["aiCostSeats"], seat.Provider, seat.Total, seat.ActiveWithinIdleWindow, seat.Idle, seat.PendingCancellation))).Append("</p>");
        }

        if (costs.Activity.Count > 0)
        {
            sb.Append("<p class=\"small\">").Append(E(string.Join(" · ", costs.Activity.Select(a => $"{a.Provider} {Local(s, a.Key)}: {a.AveragePerDay.ToString("0.#", c)}/{s["aiPerDay"]}")))).Append("</p>");
        }

        sb.Append("<p class=\"muted small\">").Append(E(s["aiCostNote"])).Append("</p>");
    }
}
