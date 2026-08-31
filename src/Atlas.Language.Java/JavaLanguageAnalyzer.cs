using System.Text.RegularExpressions;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;

namespace Atlas.Language.Java;

/// <summary>
/// Java adapter, Tier 1 (syntactic, no compiler): a hand-rolled lexer strips comments (and,
/// for structure, string literals) so a structural pass can extract packages, types, methods,
/// cyclomatic complexity, test methods and a first set of security patterns — the same
/// normalized facts the C#/VB adapters emit, so inventory, quality metrics, hot methods and
/// duplication cover Java estates too. No parser, no build, no execution; the semantic tier
/// (symbol resolution) is a future sidecar. Maven/Gradle manifests are judged by the Java
/// platform scanner, not here — this adapter deliberately emits no ProjectFacts so .NET
/// project heuristics (test coverage, migration blockers) never see Java modules.
/// </summary>
public sealed partial class JavaLanguageAnalyzer : ILanguageAnalyzer
{
    public const int HotMethodThreshold = 10;
    private const int MaxPatternsPerFile = 50;

    private static readonly HashSet<string> NotMethodNames = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "switch", "catch", "synchronized", "return", "new", "do", "try", "record", "assert", "throw",
    };

    private static readonly HashSet<string> ModifierTokens = new(StringComparer.Ordinal)
    {
        "public", "protected", "private", "static", "final", "abstract", "synchronized", "native", "strictfp", "default",
    };

    public LanguageDescriptor Descriptor { get; } = new(
        LanguageId: "java",
        Name: "Java",
        Version: "0.1.0",
        Capabilities: ["SyntaxScan", "Metrics", "SecurityPatterns", "TestDetection"]);

    public bool CanAnalyze(IArtifactReader workspace) => workspace.SourceFiles("*.java").Any();

    public async Task<LanguageAnalysisResult> AnalyzeAsync(IArtifactReader workspace, CancellationToken cancellationToken)
    {
        var files = new List<FileFact>();
        var patterns = new List<PatternFact>();
        var hotMethods = new List<MethodFact>();
        var types = new List<TypeFact>();
        var complexities = new List<int>();

        foreach (var relativePath in workspace.SourceFiles("*.java"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsBuildOutputPath(relativePath))
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
            // literals) and fully stripped (structure must not trip on braces inside strings).
            var noComments = JavaLexer.Strip(text, stripStrings: false);
            var structural = JavaLexer.Strip(text, stripStrings: true);

            var packageName = PackageRegex().Match(structural) is { Success: true } p ? p.Groups[1].Value : "<default>";
            var fileTypes = TypeRegex().Matches(structural)
                .Select(m => (Index: m.Index, Kind: m.Groups[1].Success ? "annotation" : m.Groups[2].Value, Name: m.Groups[3].Value))
                .ToList();
            foreach (var t in fileTypes)
            {
                types.Add(new TypeFact(relativePath, packageName, t.Name, t.Kind));
            }

            var methods = ScanMethods(structural, fileTypes);
            var fileMax = 0;
            foreach (var method in methods)
            {
                complexities.Add(method.Complexity);
                fileMax = Math.Max(fileMax, method.Complexity);
                if (method.Complexity >= HotMethodThreshold)
                {
                    hotMethods.Add(new MethodFact(relativePath, method.Symbol, method.Line, method.Complexity, method.BodyLines));
                }
            }

            files.Add(new FileFact(
                RelativePath: relativePath,
                Lines: CountLines(text),
                TypeCount: fileTypes.Count,
                MethodCount: methods.Count,
                MaxCyclomaticComplexity: fileMax,
                HasSyntaxErrors: false,
                TestMethodCount: TestAnnotationRegex().Matches(structural).Count));

            patterns.AddRange(CollectPatterns(noComments, relativePath, fileTypes));
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

    private sealed record MethodInfo(string Symbol, int Line, int Complexity, int BodyLines);

    /// <summary>
    /// Method headers by shape (modifiers? returnType name(args) … "{"), then brace matching for
    /// the body. Constructors have no return type token and are skipped; control-flow keywords
    /// that look like calls (if/for/while…) are excluded by name.
    /// </summary>
    private static List<MethodInfo> ScanMethods(string structural, List<(int Index, string Kind, string Name)> fileTypes)
    {
        var methods = new List<MethodInfo>();
        foreach (Match match in MethodHeaderRegex().Matches(structural))
        {
            var name = match.Groups["name"].Value;
            var typeToken = match.Groups["type"].Value;
            // "new Runnable() {" (anonymous class) and "public Foo(" (constructor via modifier
            // backtracking into the type slot) are not methods.
            if (NotMethodNames.Contains(name) || NotMethodNames.Contains(typeToken) || ModifierTokens.Contains(typeToken))
            {
                continue;
            }

            var braceIndex = structural.IndexOf('{', match.Index + match.Length - 1);
            if (braceIndex < 0)
            {
                continue;
            }

            var bodyEnd = MatchBrace(structural, braceIndex);
            if (bodyEnd < 0)
            {
                continue;
            }

            var body = structural[braceIndex..bodyEnd];
            var complexity = 1 + DecisionRegex().Matches(body).Count;
            var owner = fileTypes.LastOrDefault(t => t.Index < match.Index).Name ?? "<global>";
            var line = LineOf(structural, match.Index);
            methods.Add(new MethodInfo($"{owner}.{name}", line, complexity, LineOf(structural, bodyEnd) - line + 1));
        }

        return methods;
    }

    private static int MatchBrace(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Security patterns the shared security scanner already judges (language-neutral rules).</summary>
    private static List<PatternFact> CollectPatterns(string noComments, string relativePath, List<(int Index, string Kind, string Name)> fileTypes)
    {
        var facts = new List<PatternFact>();
        void Add(string patternId, int index, string detail)
        {
            if (facts.Count >= MaxPatternsPerFile)
            {
                return;
            }

            var owner = fileTypes.LastOrDefault(t => t.Index < index).Name ?? "<global>";
            facts.Add(new PatternFact(patternId, relativePath, LineOf(noComments, index), owner, detail));
        }

        foreach (Match m in WeakHashRegex().Matches(noComments))
        {
            Add(SecurityPatternIds.WeakHash, m.Index, $"MessageDigest.getInstance(\"{m.Groups[1].Value}\")");
        }

        foreach (Match m in SqlConcatRegex().Matches(noComments))
        {
            Add(SecurityPatternIds.SqlStringConcatenation, m.Index, $"{m.Groups[1].Value}(…) with string concatenation");
        }

        return facts;
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

    private static bool IsBuildOutputPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/target/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("target/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/build/generated/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.gradle/", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"(?m)^\s*package\s+([\w.]+)\s*;")]
    private static partial Regex PackageRegex();

    [GeneratedRegex(@"(?:\B(@interface)|\b(class|interface|enum|record))\s+(\w+)")]
    private static partial Regex TypeRegex();

    // Annotations (with args) before modifiers, one paren level in args, one nesting level in
    // generic return types; 2s match timeout so a pathological file cannot hang the scan.
    [GeneratedRegex(@"(?m)^[ \t]*(?:@\w+(?:\([^()\n]*\))?[ \t]+)*(?:(?:public|protected|private|static|final|abstract|synchronized|native|strictfp|default)[ \t]+)*(?:<[^<>;{}]{1,100}>[ \t]*)?(?<type>[\w.$]+)(?:<(?:[^<>;{}]|<[^<>;{}]{0,80}>){0,140}>)?(?:\[\])*[ \t]+(?<name>\w+)[ \t]*\((?:[^;{}()]|\([^()]{0,200}\))*\)[ \t]*(?:throws[ \t]+[\w.,\s]+)?\{", RegexOptions.None, 2000)]
    private static partial Regex MethodHeaderRegex();

    [GeneratedRegex(@"\b(if|for|while|case|catch)\b|&&|\|\|")]
    private static partial Regex DecisionRegex();

    [GeneratedRegex(@"@(Test|ParameterizedTest|RepeatedTest|TestFactory)\b")]
    private static partial Regex TestAnnotationRegex();

    [GeneratedRegex(@"MessageDigest\s*\.\s*getInstance\s*\(\s*""(MD5|SHA-?1)""")]
    private static partial Regex WeakHashRegex();

    [GeneratedRegex(@"\.\s*(executeQuery|executeUpdate|execute|prepareStatement|addBatch|nativeQuery|createQuery)\s*\((?=[^)\n]*""[^)\n]*\+)")]
    private static partial Regex SqlConcatRegex();
}

/// <summary>
/// Line-preserving lexer: removes // and /* */ comments; optionally also blanks string and char
/// literals (structure passes must not trip on braces inside strings). Newlines survive so every
/// index still maps to the original line number.
/// </summary>
internal static class JavaLexer
{
    public static string Strip(string text, bool stripStrings)
    {
        var result = new char[text.Length];
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    result[i++] = ' ';
                }
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                result[i++] = ' ';
                result[i++] = ' ';
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    result[i] = text[i] == '\n' ? '\n' : ' ';
                    i++;
                }

                if (i < text.Length)
                {
                    result[i++] = ' ';
                    result[i++] = ' ';
                }
            }
            else if (c is '"' or '\'')
            {
                // Text blocks (three quotes) collapse like ordinary strings: scan to the closer.
                var isTextBlock = c == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"';
                var quote = c;
                if (isTextBlock)
                {
                    result[i++] = stripStrings ? ' ' : '"';
                    result[i++] = stripStrings ? ' ' : '"';
                    result[i++] = stripStrings ? ' ' : '"';
                    while (i + 2 < text.Length && !(text[i] == '"' && text[i + 1] == '"' && text[i + 2] == '"'))
                    {
                        result[i] = text[i] == '\n' ? '\n' : (stripStrings ? ' ' : text[i]);
                        i++;
                    }

                    for (var k = 0; k < 3 && i < text.Length; k++)
                    {
                        result[i++] = stripStrings ? ' ' : '"';
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

                        result[i] = text[i] == '\n' ? '\n' : (stripStrings ? ' ' : text[i]);
                        i++;
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
