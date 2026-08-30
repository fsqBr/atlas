using System.Text.RegularExpressions;

namespace Atlas.Domain.Workspaces;

/// <summary>
/// Paths the customer (or Atlas by default) wants out of the analysis:
/// vendored code, minified bundles, generated files. Gitignore-like globs
/// (`vendor/`, `**/*.min.js`, `/spikes/targets/**`, `legacy-copy`) read from
/// an `.atlasignore` at the workspace root plus per-assessment patterns.
/// Applied while walking the tree, so excluded folders are never enumerated.
/// </summary>
public sealed class PathExclusions
{
    public const string IgnoreFileName = ".atlasignore";

    /// <summary>Never the customer's own code; always excluded unless a pattern is negated with a leading '!'.</summary>
    public static readonly IReadOnlyList<string> DefaultGlobs =
    [
        "vendor/", "vendors/", "third-party/", "third_party/", "ThirdParty/", "external/",
        "**/*.min.js", "**/*.min.css", "**/*.map", "**/*.bundle.js",
    ];

    private readonly List<(Regex Pattern, bool Negated)> _rules;

    private PathExclusions(List<(Regex, bool)> rules)
    {
        _rules = rules;
    }

    public static PathExclusions None { get; } = new([]);

    public IReadOnlyList<string> Globs { get; private init; } = [];

    /// <summary>Compiles defaults + the given globs (later patterns win; '!' negates).</summary>
    public static PathExclusions Compile(IEnumerable<string>? globs, bool includeDefaults = true)
    {
        var all = (includeDefaults ? DefaultGlobs : []).Concat(globs ?? []).Select(g => g.Trim())
            .Where(g => g.Length > 0 && !g.StartsWith('#')).ToList();
        var rules = new List<(Regex, bool)>();
        foreach (var glob in all)
        {
            var negated = glob.StartsWith('!');
            var body = negated ? glob[1..] : glob;
            rules.Add((ToRegex(body), negated));
        }

        return new PathExclusions(rules) { Globs = all };
    }

    /// <summary>Lines of an .atlasignore file (comments and blanks ignored).</summary>
    public static IReadOnlyList<string> ParseIgnoreFile(string? content) =>
        (content ?? string.Empty).Split('\n').Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    /// <summary>True when the relative path (file, or directory given with a trailing '/') is excluded.</summary>
    public bool IsExcluded(string relativePath)
    {
        if (_rules.Count == 0)
        {
            return false;
        }

        var path = relativePath.Replace('\\', '/').TrimStart('.', '/');
        var excluded = false;
        foreach (var (pattern, negated) in _rules)
        {
            if (pattern.IsMatch(path))
            {
                excluded = !negated;
            }
        }

        return excluded;
    }

    /// <summary>A directory is excluded when the directory itself matches (so it is pruned, never walked).</summary>
    public bool IsDirectoryExcluded(string relativeDirectory) => IsExcluded(relativeDirectory.Replace('\\', '/').TrimEnd('/') + "/");

    internal static Regex ToRegex(string glob)
    {
        var g = glob.Replace('\\', '/');
        var directoryOnly = g.EndsWith('/');
        g = g.Trim('/');
        var anchored = glob.StartsWith('/');
        var hasSlash = g.Contains('/');

        var sb = new System.Text.StringBuilder("^");
        sb.Append(anchored || hasSlash ? string.Empty : "(?:.*/)?");
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*')
            {
                if (i + 1 < g.Length && g[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < g.Length && g[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        // A directory pattern matches the directory and everything below it; a file pattern matches exactly
        // (or, when it names a directory that exists in the tree, everything below too).
        sb.Append(directoryOnly ? "(?:/.*)?$" : "(?:/.*)?$");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
