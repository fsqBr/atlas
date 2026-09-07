using System.Text.RegularExpressions;

namespace Atlas.Scanner.Licenses;

public enum LicenseClass
{
    Unknown = 0,
    Permissive = 1,
    WeakCopyleft = 2,
    StrongCopyleft = 3,
    Restricted = 4,
}

/// <summary>
/// SPDX expressions (and the usual license URLs) → a compliance class. "OR" picks the
/// most permissive option (the consumer chooses), "AND" the most restrictive. Nothing
/// here is legal advice: it flags what needs a human look.
/// </summary>
public static partial class LicenseClassifier
{
    private static readonly Dictionary<string, LicenseClass> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MIT"] = LicenseClass.Permissive, ["MIT-0"] = LicenseClass.Permissive, ["Apache-2.0"] = LicenseClass.Permissive, ["Apache-1.1"] = LicenseClass.Permissive,
        ["BSD-2-Clause"] = LicenseClass.Permissive, ["BSD-3-Clause"] = LicenseClass.Permissive, ["BSD-3-Clause-Clear"] = LicenseClass.Permissive, ["0BSD"] = LicenseClass.Permissive,
        ["ISC"] = LicenseClass.Permissive, ["MS-PL"] = LicenseClass.Permissive, ["Unlicense"] = LicenseClass.Permissive, ["Zlib"] = LicenseClass.Permissive, ["BSL-1.0"] = LicenseClass.Permissive,
        ["CC0-1.0"] = LicenseClass.Permissive, ["WTFPL"] = LicenseClass.Permissive, ["PostgreSQL"] = LicenseClass.Permissive, ["Artistic-2.0"] = LicenseClass.Permissive,
        ["Python-2.0"] = LicenseClass.Permissive, ["X11"] = LicenseClass.Permissive, ["Ms-PL"] = LicenseClass.Permissive, ["CC-BY-4.0"] = LicenseClass.Permissive, ["CC-BY-3.0"] = LicenseClass.Permissive,
        ["LGPL-2.0-only"] = LicenseClass.WeakCopyleft, ["LGPL-2.0-or-later"] = LicenseClass.WeakCopyleft, ["LGPL-2.1-only"] = LicenseClass.WeakCopyleft, ["LGPL-2.1-or-later"] = LicenseClass.WeakCopyleft,
        ["LGPL-3.0-only"] = LicenseClass.WeakCopyleft, ["LGPL-3.0-or-later"] = LicenseClass.WeakCopyleft, ["LGPL-2.1"] = LicenseClass.WeakCopyleft, ["LGPL-3.0"] = LicenseClass.WeakCopyleft,
        ["MPL-2.0"] = LicenseClass.WeakCopyleft, ["MPL-1.1"] = LicenseClass.WeakCopyleft, ["EPL-1.0"] = LicenseClass.WeakCopyleft, ["EPL-2.0"] = LicenseClass.WeakCopyleft,
        ["CDDL-1.0"] = LicenseClass.WeakCopyleft, ["CDDL-1.1"] = LicenseClass.WeakCopyleft, ["MS-RL"] = LicenseClass.WeakCopyleft, ["Ms-RL"] = LicenseClass.WeakCopyleft, ["CPL-1.0"] = LicenseClass.WeakCopyleft,
        ["GPL-2.0-only"] = LicenseClass.StrongCopyleft, ["GPL-2.0-or-later"] = LicenseClass.StrongCopyleft, ["GPL-3.0-only"] = LicenseClass.StrongCopyleft, ["GPL-3.0-or-later"] = LicenseClass.StrongCopyleft,
        ["GPL-2.0"] = LicenseClass.StrongCopyleft, ["GPL-3.0"] = LicenseClass.StrongCopyleft, ["AGPL-3.0-only"] = LicenseClass.StrongCopyleft, ["AGPL-3.0-or-later"] = LicenseClass.StrongCopyleft, ["AGPL-3.0"] = LicenseClass.StrongCopyleft,
        ["GPL-2.0-with-classpath-exception"] = LicenseClass.WeakCopyleft, ["CC-BY-SA-4.0"] = LicenseClass.StrongCopyleft, ["EUPL-1.2"] = LicenseClass.StrongCopyleft, ["OSL-3.0"] = LicenseClass.StrongCopyleft,
        ["SSPL-1.0"] = LicenseClass.Restricted, ["BUSL-1.1"] = LicenseClass.Restricted, ["CC-BY-NC-4.0"] = LicenseClass.Restricted, ["CC-BY-NC-SA-4.0"] = LicenseClass.Restricted,
        ["MS-NET-Library"] = LicenseClass.Permissive, ["Elastic-2.0"] = LicenseClass.Restricted, ["Commons-Clause"] = LicenseClass.Restricted, ["JSON"] = LicenseClass.Restricted, ["Proprietary"] = LicenseClass.Restricted, ["LicenseRef-Proprietary"] = LicenseClass.Restricted,
    };

    private static readonly (string Needle, string Spdx)[] UrlHints =
    [
        ("opensource.org/licenses/mit", "MIT"), ("mit-license", "MIT"), ("apache.org/licenses/license-2.0", "Apache-2.0"), ("opensource.org/licenses/apache-2.0", "Apache-2.0"),
        ("opensource.org/licenses/bsd-3", "BSD-3-Clause"), ("opensource.org/licenses/bsd-2", "BSD-2-Clause"), ("opensource.org/licenses/isc", "ISC"), ("opensource.org/licenses/ms-pl", "MS-PL"),
        ("opensource.org/licenses/ms-rl", "MS-RL"), ("gnu.org/licenses/lgpl", "LGPL-3.0"), ("gnu.org/licenses/agpl", "AGPL-3.0"), ("gnu.org/licenses/gpl", "GPL-3.0"), ("gnu.org/licenses/old-licenses/gpl-2.0", "GPL-2.0"),
        ("mozilla.org/mpl/2.0", "MPL-2.0"), ("eclipse.org/legal/epl", "EPL-1.0"), ("creativecommons.org/publicdomain/zero", "CC0-1.0"), ("unlicense.org", "Unlicense"),
        ("dotnet/corefx/blob/master/license", "MIT"), ("dotnet/runtime/blob/main/license", "MIT"), ("jamesnk/newtonsoft.json", "MIT"), ("newtonsoft.json/master/license", "MIT"),
        ("automapper/blob/master/license", "MIT"), ("dapper/blob/master/license", "Apache-2.0"), ("serilog/blob/master/license", "Apache-2.0"), ("nlog/blob/master/license", "BSD-3-Clause"),
        ("go.microsoft.com/fwlink/?linkid=329770", "MS-NET-Library"), ("go.microsoft.com/fwlink/?linkid=320539", "MS-NET-Library"), ("go.microsoft.com/fwlink/?linkid=214339", "MS-NET-Library"),
        // fwlink is a generic redirector: only the LinkIDs enumerated above are the .NET Library EULA.
        // Anything else (proprietary EULAs of legacy packages included) must surface as Unknown for review.
        ("go.microsoft.com/fwlink/?linkid=262998", "MS-NET-Library"), ("microsoft.com/web/webpi/eula", "Proprietary"),
        ("licenses.nuget.org/", "nuget-license-url"),
    ];

    [GeneratedRegex(@"\s+(OR|AND)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Operators();

    [GeneratedRegex(@"\s+OR\s+", RegexOptions.IgnoreCase)]
    private static partial Regex OrSplit();

    [GeneratedRegex(@"\s+AND\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AndSplit();

    public static LicenseClass Classify(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return LicenseClass.Unknown;
        }

        var text = expression.Trim().Trim('(', ')');
        // The splits are regex-based and case-insensitive: legacy npm metadata writes "MIT or GPL-2.0",
        // and a case-sensitive Split silently classified the whole string as its first token.
        var orParts = OrSplit().Split(text).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
        if (orParts.Length > 1)
        {
            // Dual licensing: the consumer picks, so the most permissive known side wins.
            return orParts.Select(Classify).Where(c => c != LicenseClass.Unknown).DefaultIfEmpty(LicenseClass.Unknown).Min();
        }

        var andParts = AndSplit().Split(text).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
        if (andParts.Length > 1)
        {
            // Every conjunct applies: an unknown term may carry any obligation, so it dominates;
            // otherwise the strictest class wins.
            var classes = andParts.Select(Classify).ToList();
            return classes.Contains(LicenseClass.Unknown) ? LicenseClass.Unknown : classes.Max();
        }

        text = text.Replace(" WITH Classpath-exception-2.0", "-with-classpath-exception", StringComparison.OrdinalIgnoreCase).Trim('(', ')');
        if (Known.TryGetValue(text, out var known))
        {
            return known;
        }

        var upper = text.ToUpperInvariant();
        if (upper.StartsWith("AGPL") || upper.StartsWith("GPL") || upper.StartsWith("SSPL") || upper.Contains("CC-BY-SA")) return LicenseClass.StrongCopyleft;
        if (upper.StartsWith("LGPL") || upper.StartsWith("MPL") || upper.StartsWith("EPL") || upper.StartsWith("CDDL") || upper.StartsWith("MS-RL")) return LicenseClass.WeakCopyleft;
        if (upper.Contains("NC") && upper.StartsWith("CC-BY")) return LicenseClass.Restricted;
        if (upper.StartsWith("BSD") || upper.StartsWith("MIT") || upper.StartsWith("APACHE") || upper.StartsWith("ISC") || upper.StartsWith("MS-PL") || upper.StartsWith("CC-BY-") || upper.StartsWith("CC0")) return LicenseClass.Permissive;
        if (upper.Contains("PROPRIETARY") || upper.Contains("COMMERCIAL") || upper.Contains("EULA") || upper.Contains("SEE LICENSE")) return LicenseClass.Restricted;
        return LicenseClass.Unknown;
    }

    /// <summary>Maps a licenseUrl (legacy nuspec / package.json) to an SPDX-ish id when it is one of the usual suspects.</summary>
    public static string? FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var lower = url.Trim().ToLowerInvariant();
        foreach (var (needle, spdx) in UrlHints)
        {
            if (lower.Contains(needle, StringComparison.Ordinal))
            {
                return spdx;
            }
        }

        return null;
    }
}
