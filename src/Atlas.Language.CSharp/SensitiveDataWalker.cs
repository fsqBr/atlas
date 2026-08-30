using Atlas.Language.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlas.Language.CSharp;

/// <summary>
/// Tier 1 (syntactic) detection of personal-data fields (.5) and of
/// places where such data flows into logs or exception messages (.6). Purely
/// name-based — member names are the strongest signal a static pass has — so
/// every fact is a candidate with the matched category; the privacy scanner
/// decides severity and confidence. Never records values, only names and places.
/// </summary>
public sealed class SensitiveDataWalker(string filePath) : CSharpSyntaxWalker
{
    private static readonly HashSet<string> LogMethods = new(StringComparer.Ordinal)
    {
        "Log", "LogInformation", "LogWarning", "LogError", "LogDebug", "LogTrace", "LogCritical",
        "Information", "Warning", "Error", "Debug", "Verbose", "Fatal", "Info", "Warn", "Trace",
        "Write", "WriteLine", "WriteAsync", "WriteLineAsync",
    };

    private readonly List<PatternFact> _facts = [];
    private readonly HashSet<(string, int, string)> _seen = [];

    public static IReadOnlyList<PatternFact> Collect(SyntaxTree tree, string filePath, CancellationToken cancellationToken)
    {
        var walker = new SensitiveDataWalker(filePath);
        walker.Visit(tree.GetRoot(cancellationToken));
        return walker._facts;
    }

    /// <summary>Category of a member name, or null (shared vocabulary with the database scanner).</summary>
    public static string? Classify(string memberName) => SensitiveNameClassifier.Classify(memberName);

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        ReportField(node.Identifier.Text, node.Type.ToString(), node);
        base.VisitPropertyDeclaration(node);
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        foreach (var variable in node.Declaration.Variables)
        {
            ReportField(variable.Identifier.Text, node.Declaration.Type.ToString(), variable);
        }

        base.VisitFieldDeclaration(node);
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        foreach (var parameter in node.ParameterList?.Parameters ?? [])
        {
            ReportField(parameter.Identifier.Text, parameter.Type?.ToString() ?? "?", parameter, node.Identifier.Text);
        }

        base.VisitRecordDeclaration(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (IsLogSink(node, out var sink))
        {
            foreach (var (name, category) in SensitiveIdentifiers(node.ArgumentList))
            {
                Report(PrivacyPatternIds.LeakToLog, node, $"{sink} ← {name} [{category}]");
            }
        }

        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var typeName = node.Type is QualifiedNameSyntax q ? q.Right.ToString() : node.Type.ToString();
        if (typeName.EndsWith("Exception", StringComparison.Ordinal) && node.ArgumentList is not null)
        {
            foreach (var (name, category) in SensitiveIdentifiers(node.ArgumentList))
            {
                Report(PrivacyPatternIds.LeakToException, node, $"new {typeName}(…) ← {name} [{category}]");
            }
        }

        base.VisitObjectCreationExpression(node);
    }

    private void ReportField(string memberName, string typeName, SyntaxNode node, string? owner = null)
    {
        var category = Classify(memberName);
        if (category is null)
        {
            return;
        }

        var type = owner ?? EnclosingTypeName(node);
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var symbol = $"{type}.{memberName}";
        var key = ($"pii.field.{category}", line, symbol);
        if (_seen.Add(key))
        {
            _facts.Add(new PatternFact($"pii.field.{category}", filePath, line, symbol, $"{memberName} : {typeName}"));
        }
    }

    private void Report(string patternId, SyntaxNode node, string detail)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (_seen.Add((patternId, line, detail)))
        {
            _facts.Add(new PatternFact(patternId, filePath, line, EnclosingMember(node), detail));
        }
    }

    private static bool IsLogSink(InvocationExpressionSyntax node, out string sink)
    {
        sink = string.Empty;
        if (node.Expression is not MemberAccessExpressionSyntax access)
        {
            return false;
        }

        var method = access.Name.Identifier.Text;
        if (!LogMethods.Contains(method))
        {
            return false;
        }

        var receiver = access.Expression.ToString();
        var receiverTail = receiver.Contains('.') ? receiver[(receiver.LastIndexOf('.') + 1)..] : receiver;
        var isLogger = receiver.Contains("log", StringComparison.OrdinalIgnoreCase)
            || receiverTail is "Console" or "Debug" or "Trace" or "Serilog" or "Log" or "Logger";
        if (!isLogger)
        {
            return false;
        }

        sink = $"{receiverTail}.{method}";
        return true;
    }

    /// <summary>Identifiers used as data (not callees, not type names) inside the arguments, with their category.</summary>
    private static IEnumerable<(string Name, string Category)> SensitiveIdentifiers(ArgumentListSyntax arguments)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in arguments.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var parent = identifier.Parent;
            if (parent is InvocationExpressionSyntax
                || parent is ObjectCreationExpressionSyntax
                || parent is TypeArgumentListSyntax
                || (parent is MemberAccessExpressionSyntax m && m.Name == identifier && m.Parent is InvocationExpressionSyntax)
                || parent is CastExpressionSyntax)
            {
                continue; // method or type names are not data
            }

            var category = Classify(identifier.Identifier.Text);
            if (category is not null && seen.Add(identifier.Identifier.Text))
            {
                yield return (identifier.Identifier.Text, category);
            }
        }
    }

    private static string EnclosingTypeName(SyntaxNode node) =>
        node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "<global>";

    private static string EnclosingMember(SyntaxNode node)
    {
        var type = EnclosingTypeName(node);
        var member = node.Ancestors().Select(a => a switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            LocalFunctionStatementSyntax l => l.Identifier.Text,
            _ => null,
        }).FirstOrDefault(n => n is not null);
        return member is null ? type : $"{type}.{member}";
    }
}
