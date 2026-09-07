using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Atlas.Language.VisualBasic;

/// <summary>
/// VB.NET adapter (006), Tier 1 syntax: files, types, methods, cyclomatic
/// complexity, test methods and a first set of security patterns — the same
/// normalized facts the C# adapter emits, so inventory, quality, architecture
/// hotspots and the modernization profile cover VB estates too. Projects and
/// solutions are read as data through the shared parsers (*.vbproj).
/// </summary>
public sealed class VisualBasicLanguageAnalyzer : ILanguageAnalyzer
{
    public const int HotMethodThreshold = 10;

    private static readonly HashSet<string> TestAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod",
    };

    public LanguageDescriptor Descriptor { get; } = new(
        LanguageId: LanguageIds.VisualBasic,
        Name: "Visual Basic .NET",
        Version: "0.1.0",
        Capabilities: ["SyntaxTree", "SolutionDiscovery", "Metrics", "SecurityPatterns", "TestDetection"]);

    public bool CanAnalyze(IArtifactReader workspace) => workspace.SourceFiles("*.vb").Any();

    public async Task<LanguageAnalysisResult> AnalyzeAsync(IArtifactReader workspace, CancellationToken cancellationToken)
    {
        var solutions = await SolutionFileParser.ParseAllAsync(workspace, cancellationToken);
        var projects = await ProjectFileParser.ParseAllAsync(workspace, cancellationToken, "*.vbproj");

        var files = new List<FileFact>();
        var patterns = new List<PatternFact>();
        var hotMethods = new List<MethodFact>();
        var types = new List<TypeFact>();
        var complexities = new List<int>();

        foreach (var relativePath in workspace.SourceFiles("*.vb"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = await workspace.ReadAllTextAsync(relativePath, cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }

            var tree = VisualBasicSyntaxTree.ParseText(text, path: relativePath, cancellationToken: cancellationToken);
            var root = await tree.GetRootAsync(cancellationToken);

            var methods = root.DescendantNodes().OfType<MethodBlockBaseSyntax>().ToList();
            var fileMax = 0;
            var tests = 0;
            foreach (var method in methods)
            {
                var complexity = VisualBasicComplexityWalker.Measure(method);
                complexities.Add(complexity);
                fileMax = Math.Max(fileMax, complexity);
                if (IsTest(method))
                {
                    tests++;
                }

                if (complexity >= HotMethodThreshold)
                {
                    var span = method.GetLocation().GetLineSpan();
                    hotMethods.Add(new MethodFact(relativePath, Symbol(method), span.StartLinePosition.Line + 1, complexity, span.EndLinePosition.Line - span.StartLinePosition.Line + 1));
                }
            }

            var typeBlocks = root.DescendantNodes().OfType<TypeBlockSyntax>().ToList();
            foreach (var type in typeBlocks)
            {
                types.Add(new TypeFact(relativePath, NamespaceOf(type), type.BlockStatement.Identifier.Text, KindOf(type)));
            }

            var enums = root.DescendantNodes().OfType<EnumBlockSyntax>().Count();
            patterns.AddRange(VisualBasicSecurityPatterns.Detect(root, relativePath));

            files.Add(new FileFact(
                relativePath,
                text.AsSpan().Count('\n') + 1,
                typeBlocks.Count + enums,
                methods.Count,
                fileMax,
                tree.GetDiagnostics(cancellationToken).Any(d => d.Severity == DiagnosticSeverity.Error),
                tests));
        }

        var totals = new LanguageTotals(
            files.Count,
            files.Sum(f => (long)f.Lines),
            files.Sum(f => f.TypeCount),
            files.Sum(f => f.MethodCount),
            complexities.Count == 0 ? 0 : complexities.Max(),
            complexities.Count == 0 ? 0 : complexities.Average());

        return new LanguageAnalysisResult(
            Descriptor.LanguageId,
            AnalysisTier.Syntactic,
            solutions,
            projects,
            files,
            totals,
            Symbols: null,
            patterns,
            hotMethods,
            types,
            NamespaceDependencies: []);
    }

    private static bool IsTest(MethodBlockBaseSyntax method) =>
        method.BlockStatement.AttributeLists
            .SelectMany(l => l.Attributes)
            .Any(a => TestAttributes.Contains(a.Name.ToString().Split('.').Last().Replace("Attribute", "")));

    internal static string Symbol(MethodBlockBaseSyntax method)
    {
        var type = method.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault();
        var name = method.BlockStatement switch
        {
            MethodStatementSyntax m => m.Identifier.Text,
            SubNewStatementSyntax => "New",
            OperatorStatementSyntax o => "Operator" + o.OperatorToken.Text,
            AccessorStatementSyntax a => a.AccessorKeyword.Text + "_" + (a.Ancestors().OfType<PropertyBlockSyntax>().FirstOrDefault()?.PropertyStatement.Identifier.Text ?? "?"),
            _ => method.BlockStatement.DeclarationKeyword.Text,
        };
        return type is null ? name : $"{type.BlockStatement.Identifier.Text}.{name}";
    }

    private static string NamespaceOf(SyntaxNode node)
    {
        var ns = node.Ancestors().OfType<NamespaceBlockSyntax>().Select(n => n.NamespaceStatement.Name.ToString()).Reverse().ToList();
        return ns.Count == 0 ? string.Empty : string.Join(".", ns);
    }

    private static string KindOf(TypeBlockSyntax type) => type switch
    {
        ClassBlockSyntax => "class",
        ModuleBlockSyntax => "module",
        StructureBlockSyntax => "struct",
        InterfaceBlockSyntax => "interface",
        _ => "type",
    };
}

/// <summary>Cyclomatic complexity for VB: 1 + decision points (If/ElseIf, loops, Case, Catch, ternaries, AndAlso/OrElse).</summary>
internal static class VisualBasicComplexityWalker
{
    public static int Measure(MethodBlockBaseSyntax method)
    {
        var complexity = 1;
        foreach (var node in method.DescendantNodes())
        {
            switch (node)
            {
                case MultiLineIfBlockSyntax:
                case ElseIfBlockSyntax:
                case SingleLineIfStatementSyntax:
                case WhileBlockSyntax:
                case ForBlockSyntax:
                case ForEachBlockSyntax:
                case DoLoopBlockSyntax:
                case CaseBlockSyntax caseBlock when !caseBlock.IsKind(SyntaxKind.CaseElseBlock):
                case CatchBlockSyntax:
                case TernaryConditionalExpressionSyntax:
                case BinaryConditionalExpressionSyntax:
                    complexity++;
                    break;
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AndAlsoExpression) || binary.IsKind(SyntaxKind.OrElseExpression):
                    complexity++;
                    break;
            }
        }

        return complexity;
    }
}

/// <summary>First VB security patterns: weak hashes, BinaryFormatter, SQL built by concatenation.</summary>
internal static class VisualBasicSecurityPatterns
{
    private static readonly string[] SqlKeywords = ["SELECT ", "INSERT ", "UPDATE ", "DELETE ", "EXEC ", "WHERE "];

    public static IEnumerable<PatternFact> Detect(SyntaxNode root, string path)
    {
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = creation.Type.ToString().Split('.').Last();
            var line = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var symbol = EnclosingSymbol(creation);
            switch (typeName)
            {
                case "MD5CryptoServiceProvider" or "SHA1CryptoServiceProvider" or "SHA1Managed" or "MD5Cng" or "SHA1Cng":
                    yield return new PatternFact(SecurityPatternIds.WeakHash, path, line, symbol, typeName);
                    break;
                case "DESCryptoServiceProvider" or "RC2CryptoServiceProvider" or "TripleDESCryptoServiceProvider":
                    yield return new PatternFact(SecurityPatternIds.WeakSymmetricCipher, path, line, symbol, typeName);
                    break;
                case "BinaryFormatter":
                    yield return new PatternFact(SecurityPatternIds.BinaryFormatter, path, line, symbol, typeName);
                    break;
                case "SqlCommand" or "OleDbCommand" or "OdbcCommand" or "SqlDataAdapter":
                    var argument = creation.ArgumentList?.Arguments.FirstOrDefault()?.GetExpression();
                    if (argument is not null && IsSqlConcatenation(argument))
                    {
                        yield return new PatternFact(SecurityPatternIds.SqlStringConcatenation, path, line, symbol, typeName + " with concatenated SQL");
                    }

                    break;
            }
        }

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentStatementSyntax>())
        {
            if (assignment.Left is MemberAccessExpressionSyntax { Name.Identifier.Text: "CommandText" } && IsSqlConcatenation(assignment.Right))
            {
                yield return new PatternFact(SecurityPatternIds.SqlStringConcatenation, path, assignment.GetLocation().GetLineSpan().StartLinePosition.Line + 1, EnclosingSymbol(assignment), "CommandText assigned from concatenated SQL");
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = invocation.Expression.ToString();
            if (name.EndsWith("MD5.Create", StringComparison.OrdinalIgnoreCase) || name.EndsWith("SHA1.Create", StringComparison.OrdinalIgnoreCase))
            {
                yield return new PatternFact(SecurityPatternIds.WeakHash, path, invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1, EnclosingSymbol(invocation), name);
            }
        }
    }

    private static bool IsSqlConcatenation(ExpressionSyntax expression)
    {
        // $"SELECT ... {id}" and String.Format("SELECT ... {0}", id) are the same injection
        // surface as & concatenation — the C# walker catches them, so VB must too.
        if (expression is InterpolatedStringExpressionSyntax interpolated)
        {
            var text = string.Concat(interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText)).ToUpperInvariant();
            return interpolated.Contents.OfType<InterpolationSyntax>().Any() && SqlKeywords.Any(k => text.Contains(k, StringComparison.Ordinal));
        }

        if (expression is InvocationExpressionSyntax formatCall
            && formatCall.Expression.ToString().EndsWith("String.Format", StringComparison.OrdinalIgnoreCase)
            && formatCall.ArgumentList is { Arguments.Count: > 1 }
            && formatCall.ArgumentList.Arguments[0] is SimpleArgumentSyntax firstArgument
            && firstArgument.Expression is LiteralExpressionSyntax formatLiteral)
        {
            var format = formatLiteral.Token.ValueText.ToUpperInvariant();
            return SqlKeywords.Any(k => format.Contains(k, StringComparison.Ordinal));
        }

        if (expression is not BinaryExpressionSyntax binary || !(binary.IsKind(SyntaxKind.ConcatenateExpression) || binary.IsKind(SyntaxKind.AddExpression)))
        {
            return false;
        }

        var literals = expression.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().Select(l => l.Token.ValueText.ToUpperInvariant());
        return literals.Any(l => SqlKeywords.Any(k => l.Contains(k, StringComparison.Ordinal)));
    }

    private static string EnclosingSymbol(SyntaxNode node)
    {
        var method = node.Ancestors().OfType<MethodBlockBaseSyntax>().FirstOrDefault();
        return method is null ? node.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault()?.BlockStatement.Identifier.Text ?? "<file>" : VisualBasicLanguageAnalyzer.Symbol(method);
    }
}
