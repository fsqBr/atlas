using System.Globalization;
using System.Text;
using Atlas.Governance.Domain;

namespace Atlas.Governance.Application;

/// <summary>
/// Usage facts as CSV for finance: one line per (day, actor, tool, model) with the team resolved at export time.
/// Formula-safe (a leading =, +, -, @ is neutralised) and UTF-8 with BOM so Excel opens it correctly.
/// </summary>
public static class UsageCsv
{
    public static string Write(IReadOnlyList<UsageFact> facts, IReadOnlyList<Team> teams)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // BOM: Excel then reads UTF-8 correctly
        sb.AppendLine("period,actor,team,tool,provider,model,input_tokens,output_tokens,cache_read_tokens,cache_write_tokens,requests,sessions,estimated_cost_usd,reported_cost_usd,price_catalog,source");
        foreach (var f in facts.OrderBy(f => f.Period).ThenBy(f => f.Actor, StringComparer.Ordinal).ThenBy(f => f.Tool, StringComparer.Ordinal).ThenBy(f => f.Model, StringComparer.Ordinal))
        {
            var team = teams.FirstOrDefault(t => t.Matches(f.Actor))?.Name ?? "";
            sb.Append(f.Period.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(Cell(f.Actor)).Append(',')
              .Append(Cell(team)).Append(',')
              .Append(Cell(f.Tool)).Append(',')
              .Append(Cell(f.Provider ?? ModelProviders.Guess(f.Model) ?? "")).Append(',')
              .Append(Cell(f.Model)).Append(',')
              .Append(f.InputTokens).Append(',')
              .Append(f.OutputTokens).Append(',')
              .Append(f.CacheReadTokens).Append(',')
              .Append(f.CacheWriteTokens).Append(',')
              .Append(f.Requests).Append(',')
              .Append(f.Sessions).Append(',')
              .Append(f.EstimatedCost?.ToString("0.####", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(f.ReportedCost?.ToString("0.####", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(Cell(f.PriceCatalogVersion)).Append(',')
              .Append(Cell(f.Source))
              .AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Quotes when needed and neutralises spreadsheet formulas (CSV injection).</summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var v = value;
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            v = "'" + v;
        }

        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
