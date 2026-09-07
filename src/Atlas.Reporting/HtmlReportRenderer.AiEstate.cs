using System.Globalization;
using System.Text;
using Atlas.Application.AiEstate;

namespace Atlas.Reporting;

/// <summary>The AI Estate section of the executive report (governance module V0.5).</summary>
public static partial class HtmlReportRenderer
{
    private static void RenderAiEstate(StringBuilder sb, ExecutiveReport r, ReportLocale l, CultureInfo c)
    {
        var ai = r.AiEstate;
        sb.Append("<section class=\"ai-estate\"><h2>").Append(E(l.AiEstateTitle)).Append("</h2>");
        if (ai is null || !ai.Scanned)
        {
            sb.Append("<p class=\"muted\">").Append(E(l.AiNotScanned)).Append("</p></section>");
            return;
        }

        var record = ai.Record;
        if (record is null)
        {
            sb.Append("<p class=\"muted\">").Append(E(l.AiNone)).Append("</p></section>");
            return;
        }

        var external = record.ExternalProviders.Count();
        sb.Append("<div class=\"tiles\">");
        Tile(sb, l.AiProviders, record.Providers.Count.ToString("N0", c), null);
        Tile(sb, l.AiSdks, record.SdkPackages.ToString("N0", c), null);
        Tile(sb, l.AiFrameworks, record.Frameworks.Count.ToString("N0", c), null);
        Tile(sb, l.AiMcpServers, record.Mcp.Count.ToString("N0", c), record.RemoteMcp > 0 ? "medium" : null);
        Tile(sb, l.AiModels, (record.ActiveModels.Count + record.RetiredModels.Count).ToString("N0", c), null);
        Tile(sb, l.AiRetiredModels, record.RetiredModels.Count.ToString("N0", c), record.RetiredModels.Count > 0 ? "medium" : null);
        if (record.AllowlistConfigured)
        {
            Tile(sb, l.AiUnapproved, record.Unapproved.Count.ToString("N0", c), record.Unapproved.Count > 0 ? "critical" : "low");
        }

        if (record.SecretsInMcp > 0)
        {
            Tile(sb, l.AiSecretsInMcp, record.SecretsInMcp.ToString("N0", c), "critical");
        }

        sb.Append("</div>");

        // The correlation is the point of the section: code that talks to a vendor, next to data that must not.
        if (external > 0)
        {
            sb.Append("<aside class=\"note\">");
            if (ai.OpenPii > 0 || ai.OpenSecrets > 0)
            {
                sb.Append(string.Format(c, l.AiCorrelation, external, ai.OpenPii, ai.OpenSecrets));
            }
            else
            {
                sb.Append(E(l.AiCorrelationClean));
            }

            sb.Append("</aside>");
        }

        // Providers.
        sb.Append("<table><thead><tr><th>").Append(E(l.AiProvider)).Append("</th><th>").Append(E(l.AiKind)).Append("</th><th>").Append(E(l.AiEvidence)).Append("</th><th>").Append(E(l.AiStatus)).Append("</th></tr></thead><tbody>");
        foreach (var p in record.Providers.OrderBy(p => p.IsExternal ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var status = !record.AllowlistConfigured ? l.AiNoAllowlist : p.Approved == false ? l.AiNotApproved : l.AiApproved;
            var statusClass = !record.AllowlistConfigured ? "muted" : p.Approved == false ? "bad" : "ok";
            sb.Append("<tr><td><strong>").Append(E(p.Name)).Append("</strong>");
            if (p.Packages.Count > 0)
            {
                sb.Append("<br><span class=\"mono small muted\">").Append(E(string.Join(", ", p.Packages.Take(4)))).Append(p.Packages.Count > 4 ? " …" : string.Empty).Append("</span>");
            }

            sb.Append("</td><td>").Append(E(l.Term(p.Kind))).Append("</td>")
              .Append("<td class=\"small\">").Append(E(string.Format(c, l.AiEvidenceFormat, p.Packages.Count, p.EndpointFiles, p.EnvVars.Count, p.Models.Count, p.CodeFiles))).Append("</td>")
              .Append("<td class=\"").Append(statusClass).Append("\">").Append(E(status)).Append("</td></tr>");
        }

        sb.Append("</tbody></table>");

        if (record.Frameworks.Count > 0)
        {
            sb.Append("<p>").Append(E(string.Format(c, l.AiFrameworksLine, string.Join(", ", record.Frameworks.Select(f => f.Name))))).Append("</p>");
        }

        var infra = record.LocalRuntimes.Concat(record.VectorStores).Concat(record.Gateways).Distinct(StringComparer.Ordinal).ToList();
        if (infra.Count > 0)
        {
            sb.Append("<p class=\"muted small\">").Append(E(string.Format(c, l.AiInfraLine, string.Join(", ", infra)))).Append("</p>");
        }

        // MCP servers.
        if (record.Mcp.Count > 0)
        {
            sb.Append("<h3>").Append(E(l.AiMcpTitle)).Append("</h3><table><thead><tr><th>").Append(E(l.AiServer)).Append("</th><th>").Append(E(l.AiConfig)).Append("</th><th>")
              .Append(E(l.AiTransport)).Append("</th><th>").Append(E(l.AiCapability)).Append("</th><th>").Append(E(l.AiHost)).Append("</th></tr></thead><tbody>");
            foreach (var m in record.Mcp)
            {
                sb.Append("<tr><td><strong>").Append(E(m.Name)).Append("</strong>");
                if (m.Known is not null)
                {
                    sb.Append("<br><span class=\"small muted\">").Append(E(m.Known)).Append("</span>");
                }

                if (m.Secrets > 0)
                {
                    sb.Append(" <span class=\"chip sev-high\">").Append(E(l.AiSecretsInMcp)).Append("</span>");
                }

                sb.Append("</td><td class=\"mono small\">").Append(E(m.Config)).Append("</td><td>").Append(E(m.Transport)).Append("</td><td>").Append(E(m.Capability)).Append("</td>")
                  .Append("<td class=\"mono small").Append(m.Remote ? " bad" : string.Empty).Append("\">").Append(E(m.Host ?? m.Command ?? "—")).Append("</td></tr>");
            }

            sb.Append("</tbody></table>");
        }

        // Retired models.
        if (record.RetiredModels.Count > 0)
        {
            sb.Append("<h3>").Append(E(l.AiRetiredTitle)).Append("</h3><table><thead><tr><th>").Append(E(l.AiModel)).Append("</th><th>").Append(E(l.AiRetiredOn)).Append("</th><th>")
              .Append(E(l.AiReplacement)).Append("</th><th class=\"num\">").Append(E(l.Files)).Append("</th></tr></thead><tbody>");
            foreach (var m in record.RetiredModels)
            {
                sb.Append("<tr><td class=\"mono\">").Append(E(m.Model)).Append("</td><td>").Append(E(m.RetiredOn ?? "—")).Append("</td><td>").Append(E(m.Replacement ?? "—")).Append("</td><td class=\"num\">").Append(m.Files.ToString("N0", c)).Append("</td></tr>");
            }

            sb.Append("</tbody></table>");
        }

        // The AI rules that fired, in the same shape as the category tables.
        if (ai.AiRuleGroups.Count > 0)
        {
            sb.Append("<h3>").Append(E(l.AiFindingsTitle)).Append("</h3><table><thead><tr><th>").Append(E(l.Severity)).Append("</th><th>").Append(E(l.Rule)).Append("</th><th class=\"num\">").Append(E(l.Open))
              .Append("</th><th>").Append(E(l.WhereSample)).Append("</th></tr></thead><tbody>");
            foreach (var g in ai.AiRuleGroups)
            {
                sb.Append("<tr><td>").Append(SeverityChip(g.MaxSeverity, l)).Append("</td>")
                  .Append("<td><strong>").Append(E(g.Title)).Append("</strong><br><span class=\"mono muted\">").Append(E(g.RuleId)).Append("</span></td>")
                  .Append("<td class=\"num\">").Append(g.OpenCount.ToString("N0", c)).Append("</td>")
                  .Append("<td class=\"mono small\">").Append(string.Join("<br>", g.SampleLocations.Select(E))).Append("</td></tr>");
            }

            sb.Append("</tbody></table>");
        }

        // Spend, when cost sources are connected (governance module).
        if (ai.Costs is { Count: > 0 })
        {
            sb.Append("<h3>").Append(E(l.AiCostTitle)).Append("</h3><table><thead><tr><th>").Append(E(l.AiProvider)).Append("</th><th>").Append(E(l.AiEvidence)).Append("</th><th>")
              .Append(E(l.AiCostPeriod)).Append("</th><th class=\"num\">").Append(E(l.AiCostAmount)).Append("</th><th>").Append(E(l.AiCostBasis)).Append("</th></tr></thead><tbody>");
            foreach (var line in ai.Costs)
            {
                sb.Append("<tr><td>").Append(E(line.Provider)).Append("</td><td>").Append(E(line.Dimension)).Append("</td><td>").Append(E(line.Period)).Append("</td>")
                  .Append("<td class=\"num\">").Append(E(Money(line.Amount, line.Currency, c))).Append("</td><td>").Append(E(line.Basis)).Append("</td></tr>");
            }

            sb.Append("</tbody></table>");
        }

        sb.Append("<p class=\"muted small\">").Append(E(string.Format(c, l.AiCatalogNote, record.CatalogVersion, record.CatalogHash.Length > 12 ? record.CatalogHash[..12] : record.CatalogHash))).Append("</p>");
        sb.Append("</section>");
    }
}
