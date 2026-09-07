using Atlas.Domain.Findings;

namespace Atlas.Scanner.Dependencies.Vulnerabilities;

/// <summary>
/// Severity from a CVSS vector string. OSV entries not curated by GitHub often carry only
/// `severity[].score` as a vector (e.g. "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H");
/// collapsing those to Medium under-claimed Criticals. CVSS 3.x is computed exactly per the
/// specification; 4.0 uses a conservative reading of the impact metrics.
/// </summary>
public static class CvssVector
{
    public static Severity? ToSeverity(string vector)
    {
        var score = BaseScore(vector);
        return score switch
        {
            null => null,
            >= 9.0 => Severity.Critical,
            >= 7.0 => Severity.High,
            >= 4.0 => Severity.Medium,
            > 0.0 => Severity.Low,
            _ => Severity.Informational,
        };
    }

    /// <summary>CVSS 3.0/3.1 base score, or an approximation for 4.0; null when the vector is unreadable.</summary>
    public static double? BaseScore(string vector)
    {
        var metrics = Parse(vector, out var version);
        if (metrics is null)
        {
            return null;
        }

        return version.StartsWith("3", StringComparison.Ordinal) ? V3Base(metrics) : V4Approximation(metrics);
    }

    private static Dictionary<string, string>? Parse(string vector, out string version)
    {
        version = string.Empty;
        var parts = vector.Trim().Split('/');
        if (parts.Length < 2 || !parts[0].StartsWith("CVSS:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        version = parts[0][5..];
        if (!version.StartsWith("3", StringComparison.Ordinal) && !version.StartsWith("4", StringComparison.Ordinal))
        {
            return null; // CVSS 2 vectors have no "CVSS:" prefix in practice; anything else is unknown
        }

        var metrics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var idx = part.IndexOf(':');
            if (idx > 0 && idx < part.Length - 1)
            {
                metrics[part[..idx]] = part[(idx + 1)..].ToUpperInvariant();
            }
        }

        return metrics.Count == 0 ? null : metrics;
    }

    private static double? V3Base(Dictionary<string, string> m)
    {
        double? av = m.GetValueOrDefault("AV") switch { "N" => 0.85, "A" => 0.62, "L" => 0.55, "P" => 0.2, _ => null };
        double? ac = m.GetValueOrDefault("AC") switch { "L" => 0.77, "H" => 0.44, _ => null };
        double? ui = m.GetValueOrDefault("UI") switch { "N" => 0.85, "R" => 0.62, _ => null };
        var changed = m.GetValueOrDefault("S") == "C";
        double? pr = (m.GetValueOrDefault("PR"), changed) switch
        {
            ("N", _) => 0.85,
            ("L", false) => 0.62,
            ("L", true) => 0.68,
            ("H", false) => 0.27,
            ("H", true) => 0.5,
            _ => null,
        };
        double? c = Cia(m.GetValueOrDefault("C")), i = Cia(m.GetValueOrDefault("I")), a = Cia(m.GetValueOrDefault("A"));
        if (av is null || ac is null || ui is null || pr is null || c is null || i is null || a is null)
        {
            return null;
        }

        var iss = 1 - (1 - c.Value) * (1 - i.Value) * (1 - a.Value);
        var impact = changed
            ? 7.52 * (iss - 0.029) - 3.25 * Math.Pow(iss - 0.02, 15)
            : 6.42 * iss;
        if (impact <= 0)
        {
            return 0;
        }

        var exploitability = 8.22 * av.Value * ac.Value * pr.Value * ui.Value;
        var score = changed
            ? Math.Min(1.08 * (impact + exploitability), 10)
            : Math.Min(impact + exploitability, 10);
        return Math.Ceiling(score * 10) / 10; // spec "roundup" to one decimal
    }

    private static double? Cia(string? value) => value switch { "H" => 0.56, "L" => 0.22, "N" => 0.0, _ => null };

    /// <summary>
    /// CVSS 4.0's macrovector scoring is out of scope; read the vulnerable-system impact
    /// metrics conservatively so a 4.0 vector at least lands in the right band.
    /// </summary>
    private static double? V4Approximation(Dictionary<string, string> m)
    {
        static int Level(string? value) => value switch { "H" => 2, "L" => 1, _ => 0 };
        var impact = Level(m.GetValueOrDefault("VC")) + Level(m.GetValueOrDefault("VI")) + Level(m.GetValueOrDefault("VA"));
        if (impact == 0 && !m.ContainsKey("VC") && !m.ContainsKey("VI") && !m.ContainsKey("VA"))
        {
            return null;
        }

        var network = m.GetValueOrDefault("AV") == "N";
        var noPriv = m.GetValueOrDefault("PR") is null or "N";
        var noUi = m.GetValueOrDefault("UI") is null or "N";
        return impact switch
        {
            >= 5 when network && noPriv && noUi => 9.5,
            >= 4 when network => 8.0,
            >= 3 => 6.5,
            >= 1 => 4.0,
            _ => 1.0,
        };
    }
}
