using System.Text.RegularExpressions;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;

namespace Atlas.Language.Python;

/// <summary>
/// Python adapter, Tier 1 (syntactic, no interpreter): a line-preserving lexer strips comments
/// (and, for structure, string literals — including triple-quoted and prefixed forms), then an
/// indentation-based pass extracts classes, functions, cyclomatic complexity, test functions and
/// a first set of security patterns — the same normalized facts the C#/VB/Java adapters emit.
/// No parser, no execution; manifests (requirements/pyproject/Pipfile) are judged by the Python
/// platform scanner, and this adapter deliberately emits no ProjectFacts so .NET project
/// heuristics never see Python packages.
/// </summary>
public sealed partial class PythonLanguageAnalyzer : ILanguageAnalyzer
{
    public const int HotMethodThreshold = 10;
    private const int MaxPatternsPerFile = 50;

    public LanguageDescriptor Descriptor { get; } = new(
        LanguageId: LanguageIds.Python,
        Name: "Python",
        Version: "0.1.0",
        Capabilities: ["SyntaxScan", "Metrics", "SecurityPatterns", "TestDetection"]);

    public bool CanAnalyze(IArtifactReader workspace) => workspace.SourceFiles("*.py").Any();

    public async Task<LanguageAnalysisResult> AnalyzeAsync(IArtifactReader workspace, CancellationToken cancellationToken)
    {
        var files = new List<FileFact>();
        var patterns = new List<PatternFact>();
        var hotMethods = new List<MethodFact>();
        var types = new List<TypeFact>();
        var complexities = new List<int>();

        foreach (var relativePath in workspace.SourceFiles("*.py"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsVendoredPath(relativePath))
            {
                continue;
            }

            string text;
            try
            {
                text = await workspace.ReadAllTextAsync(relativePath, cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }

            try
            {
            // Two lexed views, both line-preserving: comments-only stripped (patterns need string
            // literals) and fully stripped (structure must not trip on keywords inside strings).
            var noComments = PythonLexer.Strip(text, stripStrings: false);
            var structural = PythonLexer.Strip(text, stripStrings: true);
            var lines = structural.Split('\n');

            var moduleName = ModuleOf(relativePath);
            var classes = new List<(int LineIndex, int Indent, string Name)>();
            foreach (Match m in ClassRegex().Matches(structural))
            {
                var lineIndex = LineOf(structural, m.Index) - 1;
                classes.Add((lineIndex, m.Groups[1].Value.Length, m.Groups[2].Value));
                types.Add(new TypeFact(relativePath, moduleName, m.Groups[2].Value, "class"));
            }

            var functions = ScanFunctions(structural, lines, classes, Path.GetFileNameWithoutExtension(relativePath));
            var fileMax = 0;
            foreach (var function in functions)
            {
                complexities.Add(function.Complexity);
                fileMax = Math.Max(fileMax, function.Complexity);
                if (function.Complexity >= HotMethodThreshold)
                {
                    hotMethods.Add(new MethodFact(relativePath, function.Symbol, function.Line, function.Complexity, function.BodyLines));
                }
            }

            files.Add(new FileFact(
                RelativePath: relativePath,
                Lines: CountLines(text),
                TypeCount: classes.Count,
                MethodCount: functions.Count,
                MaxCyclomaticComplexity: fileMax,
                HasSyntaxErrors: false,
                TestMethodCount: functions.Count(f => f.Name.StartsWith("test_", StringComparison.Ordinal) || f.Name == "test")));

            patterns.AddRange(CollectPatterns(noComments, relativePath, classes, structural));
            }
            catch (RegexMatchTimeoutException)
            {
                continue; // pathological input: skip the file, never hang the scan
            }
        }

        var totals = new LanguageTotals(
            FileCount: files.Count,
            TotalLines: files.Sum(f => (long)f.Lines),
            TypeCount: files.Sum(f => f.TypeCount),
            MethodCount: files.Sum(f => f.MethodCount),
            MaxCyclomaticComplexity: complexities.Count == 0 ? 0 : complexities.Max(),
            AverageCyclomaticComplexity: complexities.Count == 0 ? 0 : complexities.Average());

        return new LanguageAnalysisResult(
            Descriptor.LanguageId, AnalysisTier.Syntactic, [], [], files, totals, null, patterns, hotMethods, types, []);
    }

    private sealed record FunctionInfo(string Symbol, string Name, int Line, int Complexity, int BodyLines);

    /// <summary>
    /// Functions by "def" at any indentation; the body is the following block with deeper
    /// indentation (blank lines pass through). The enclosing class is the nearest preceding
    /// class header with a smaller indent.
    /// </summary>
    private static List<FunctionInfo> ScanFunctions(string structural, string[] lines, List<(int LineIndex, int Indent, string Name)> classes, string fileStem)
    {
        var functions = new List<FunctionInfo>();
        foreach (Match match in DefRegex().Matches(structural))
        {
            var name = match.Groups[2].Value;
            var indent = match.Groups[1].Value.Length;
            var lineIndex = LineOf(structural, match.Index) - 1;

            // Multi-line signatures (Black closes them with "):" at the def's own indent) end
            // where the def's parenthesis balances — the body starts AFTER that line, or the
            // ")" line would terminate the body scan before it ever saw the body.
            var parenIndex = structural.IndexOf('(', match.Index);
            var depth = 0;
            var closeIndex = parenIndex;
            for (; closeIndex < structural.Length; closeIndex++)
            {
                if (structural[closeIndex] == '(')
                {
                    depth++;
                }
                else if (structural[closeIndex] == ')' && --depth == 0)
                {
                    break;
                }
            }

            var signatureEndLine = closeIndex < structural.Length ? LineOf(structural, closeIndex) - 1 : lineIndex;

            var bodyEnd = signatureEndLine;
            for (var i = signatureEndLine + 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                if (IndentOf(line) <= indent)
                {
                    break;
                }

                bodyEnd = i;
            }

            var body = string.Join('\n', lines[(signatureEndLine + 1)..(bodyEnd + 1)]);
            var complexity = 1 + DecisionRegex().Matches(body).Count;
            var owner = classes.LastOrDefault(c => c.LineIndex < lineIndex && c.Indent < indent).Name ?? fileStem;
            functions.Add(new FunctionInfo($"{owner}.{name}", name, lineIndex + 1, complexity, Math.Max(1, bodyEnd - signatureEndLine)));
        }

        return functions;
    }

    /// <summary>Security patterns the shared security scanner already judges (language-neutral rules).</summary>
    private static List<PatternFact> CollectPatterns(string noComments, string relativePath, List<(int LineIndex, int Indent, string Name)> classes, string structural)
    {
        var facts = new List<PatternFact>();
        void Add(string patternId, int index, string detail)
        {
            if (facts.Count >= MaxPatternsPerFile)
            {
                return;
            }

            var lineIndex = LineOf(noComments, index) - 1;
            var owner = classes.LastOrDefault(c => c.LineIndex < lineIndex).Name ?? "<module>";
            facts.Add(new PatternFact(patternId, relativePath, lineIndex + 1, owner, detail));
        }

        foreach (Match m in WeakHashRegex().Matches(noComments))
        {
            var algorithm = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            Add(SecurityPatternIds.WeakHash, m.Index, $"hashlib {algorithm}");
        }

        foreach (Match m in SqlConcatRegex().Matches(noComments))
        {
            Add(SecurityPatternIds.SqlStringConcatenation, m.Index, $"{m.Groups[1].Value}(…) with string building (+/%/format/f-string)");
        }

        return facts;
    }

    /// <summary>Dotted module path from the relative file path ("src/app/models.py" → "src.app").</summary>
    private static string ModuleOf(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath.Replace('\\', '/'))?.Replace('\\', '/');
        return string.IsNullOrEmpty(directory) ? "<root>" : directory.Replace('/', '.');
    }

    private static int IndentOf(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        return i;
    }

    private static int CountLines(string text) => text.Count(c => c == '\n') + 1;

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static bool IsVendoredPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/site-packages/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.tox/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.eggs/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(".egg-info/", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"(?m)^([ \t]*)class[ \t]+(\w+)", RegexOptions.None, 2000)]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"(?m)^([ \t]*)(?:async[ \t]+)?def[ \t]+(\w+)[ \t]*\(", RegexOptions.None, 2000)]
    private static partial Regex DefRegex();

    [GeneratedRegex(@"\b(if|elif|for|while|except|and|or)\b", RegexOptions.None, 2000)]
    private static partial Regex DecisionRegex();

    [GeneratedRegex(@"hashlib\s*\.\s*(md5|sha1)\s*\(|hashlib\s*\.\s*new\s*\(\s*[""'](md5|sha1)[""']", RegexOptions.None, 2000)]
    private static partial Regex WeakHashRegex();

    [GeneratedRegex(@"\.\s*(execute|executemany|executescript)\s*\((?=[^)\n]*(?:[""'][ \t]*[%+]|[%+][ \t]*[""']|f[""']|\.format\(|\+[ \t]*[A-Za-z_]))", RegexOptions.None, 2000)]
    private static partial Regex SqlConcatRegex();
}

/// <summary>
/// Line-preserving lexer: removes # comments; optionally also blanks string literals — single,
/// double, triple-quoted and prefixed (r/b/f/u in any case or combination). Escapes keep a
/// backslash-quote from closing the string; newlines survive so indexes still map to lines.
/// </summary>
internal static class PythonLexer
{
    public static string Strip(string text, bool stripStrings)
    {
        var result = new char[text.Length];
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    result[i++] = ' ';
                }
            }
            else if (c is '"' or '\'')
            {
                var quote = c;
                var isTriple = i + 2 < text.Length && text[i + 1] == quote && text[i + 2] == quote;
                if (isTriple)
                {
                    result[i++] = stripStrings ? ' ' : quote;
                    result[i++] = stripStrings ? ' ' : quote;
                    result[i++] = stripStrings ? ' ' : quote;
                    while (i + 2 < text.Length && !(text[i] == quote && text[i + 1] == quote && text[i + 2] == quote))
                    {
                        result[i] = text[i] == '\n' ? '\n' : (stripStrings ? ' ' : text[i]);
                        i++;
                    }

                    for (var k = 0; k < 3 && i < text.Length; k++)
                    {
                        result[i] = text[i] == '\n' ? '\n' : (stripStrings ? ' ' : quote);
                        i++;
                    }
                }
                else
                {
                    result[i++] = stripStrings ? ' ' : quote;
                    while (i < text.Length && text[i] != quote && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            result[i] = text[i] == '\n' ? '\n' : (stripStrings ? ' ' : text[i]);
                            i++;
                        }

                        if (i < text.Length)
                        {
                            result[i] = text[i] == '\n' ? '\n' : (stripStrings ? ' ' : text[i]);
                            i++;
                        }
                    }

                    if (i < text.Length)
                    {
                        result[i] = text[i] == '\n' ? '\n' : (stripStrings ? ' ' : quote);
                        i++;
                    }
                }
            }
            else
            {
                result[i++] = c;
            }
        }

        return new string(result);
    }
}
