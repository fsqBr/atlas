using System.Security.Cryptography;
using System.Text;

namespace Atlas.Scanner.Quality;

public sealed record DuplicateBlock(string FilePath, int StartLine, int Lines, string Hash, IReadOnlyList<string> OtherLocations);

/// <summary>
/// Copy-paste detection over normalized source lines (.8
/// "duplication"): sliding windows of N lines are hashed; windows that occur in
/// two or more places are merged into blocks per location. Deterministic and
/// language-agnostic; comments, blank lines, usings and brace-only lines are
/// ignored so formatting differences do not hide duplicates.
/// </summary>
public static class DuplicationDetector
{
    public const int WindowLines = 8;
    public const int MinBlockLines = 12;

    public static IReadOnlyList<DuplicateBlock> Detect(IReadOnlyDictionary<string, string> filesByPath, int windowLines = WindowLines, int minBlockLines = MinBlockLines)
    {
        // 1. Normalize every file into (line number, content) pairs.
        var normalized = new Dictionary<string, List<(int Line, string Text)>>(StringComparer.Ordinal);
        foreach (var (path, content) in filesByPath)
        {
            normalized[path] = Normalize(content);
        }

        // 2. Hash windows; index hash → occurrences.
        var index = new Dictionary<string, List<(string Path, int Index)>>(StringComparer.Ordinal);
        foreach (var (path, lines) in normalized)
        {
            for (var i = 0; i + windowLines <= lines.Count; i++)
            {
                var hash = HashWindow(lines, i, windowLines);
                (index.TryGetValue(hash, out var list) ? list : index[hash] = []).Add((path, i));
            }
        }

        var duplicated = index.Where(kv => kv.Value.Select(o => (o.Path, o.Index)).Distinct().Count() >= 2 && kv.Value.Select(o => o.Path).Distinct().Count() >= 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // 3. Merge consecutive duplicated windows per file into blocks.
        var blocks = new List<DuplicateBlock>();
        foreach (var (path, lines) in normalized)
        {
            var i = 0;
            while (i + windowLines <= lines.Count)
            {
                var hash = HashWindow(lines, i, windowLines);
                if (!duplicated.ContainsKey(hash))
                {
                    i++;
                    continue;
                }

                var start = i;
                var others = new Dictionary<string, int>(StringComparer.Ordinal);
                var firstHash = hash;
                HashSet<string>? partners = null;
                while (i + windowLines <= lines.Count && duplicated.TryGetValue(HashWindow(lines, i, windowLines), out var occurrences))
                {
                    // A block only extends while it keeps at least one partner in common: two adjacent
                    // duplicates with different partners are two findings, not one oversized block that
                    // exists in neither partner file.
                    var windowPartners = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (otherPath, otherIndex) in occurrences)
                    {
                        if (otherPath != path || otherIndex < start || otherIndex >= i + windowLines)
                        {
                            windowPartners.Add(otherPath);
                        }
                    }

                    if (partners is null)
                    {
                        partners = windowPartners;
                    }
                    else
                    {
                        partners.IntersectWith(windowPartners);
                        if (partners.Count == 0)
                        {
                            break;
                        }
                    }

                    foreach (var (otherPath, otherIndex) in occurrences)
                    {
                        if ((otherPath != path || otherIndex < start || otherIndex >= i + windowLines) && partners.Contains(otherPath))
                        {
                            // One entry per other file: the earliest line of the matching block.
                            var otherLine = normalized[otherPath][otherIndex].Line;
                            if (!others.TryGetValue(otherPath, out var existing) || otherLine < existing)
                            {
                                others[otherPath] = otherLine;
                            }
                        }
                    }

                    i++;
                }

                var end = Math.Min(lines.Count - 1, i - 1 + windowLines - 1);
                var blockLines = lines[end].Line - lines[start].Line + 1;
                // Gate on lines that are actually duplicated (normalized), not the raw span: blank and
                // comment lines the normalization skipped are presentation, not duplication.
                var normalizedLines = (i - start) + windowLines - 1;
                var reported = others.Where(o => partners is not null && partners.Contains(o.Key))
                    .OrderBy(o => o.Key, StringComparer.Ordinal).Take(5).Select(o => $"{o.Key}:{o.Value}").ToList();
                if (normalizedLines >= minBlockLines && reported.Count > 0)
                {
                    blocks.Add(new DuplicateBlock(path, lines[start].Line, blockLines, firstHash, reported));
                }
            }
        }

        return blocks.OrderByDescending(b => b.Lines).ThenBy(b => b.FilePath, StringComparer.Ordinal).ThenBy(b => b.StartLine).ToList();
    }

    internal static List<(int Line, string Text)> Normalize(string content)
    {
        var result = new List<(int, string)>();
        var lineNumber = 0;
        var inBlockComment = false;
        foreach (var raw in content.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim().TrimEnd('\r');

            if (inBlockComment)
            {
                if (line.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = false;
                }

                continue;
            }

            if (line.StartsWith("/*", StringComparison.Ordinal))
            {
                inBlockComment = !line.Contains("*/", StringComparison.Ordinal);
                continue;
            }

            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal)
                || line.StartsWith("using ", StringComparison.Ordinal) || line.StartsWith("namespace ", StringComparison.Ordinal)
                || line is "{" or "}" or "};" or "});" or "})" or "[" or "]" or "else" or "try" or "finally")
            {
                continue;
            }

            result.Add((lineNumber, string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries))));
        }

        return result;
    }

    private static string HashWindow(List<(int Line, string Text)> lines, int start, int count)
    {
        var sb = new StringBuilder();
        for (var i = start; i < start + count; i++)
        {
            sb.Append(lines[i].Text).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
