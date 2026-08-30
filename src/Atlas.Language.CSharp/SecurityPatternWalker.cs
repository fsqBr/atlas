using Atlas.Language.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlas.Language.CSharp;

/// <summary>
/// Tier 1 (syntactic) detection of high-signal insecure patterns. Deliberately
/// conservative: it reports *candidates* with the API that triggered them; the
/// security scanner assigns severity/confidence and the report never claims
/// exploitability (.3). Facts are de-duplicated per (pattern, line).
/// </summary>
internal sealed class SecurityPatternWalker(string filePath) : CSharpSyntaxWalker
{
    private static readonly HashSet<string> WeakHashTypes = new(StringComparer.Ordinal)
    {
        "MD5CryptoServiceProvider", "SHA1CryptoServiceProvider", "SHA1Managed", "MD5Cng", "SHA1Cng",
    };

    private static readonly HashSet<string> WeakCipherTypes = new(StringComparer.Ordinal)
    {
        "DESCryptoServiceProvider", "TripleDESCryptoServiceProvider", "RC2CryptoServiceProvider", "TripleDESCng",
    };

    private static readonly HashSet<string> SqlCommandTypes = new(StringComparer.Ordinal)
    {
        "SqlCommand", "OleDbCommand", "OdbcCommand", "OracleCommand", "MySqlCommand", "NpgsqlCommand", "SqliteCommand",
    };

    private static readonly HashSet<string> WeakHashFactories = new(StringComparer.Ordinal) { "MD5.Create", "SHA1.Create" };
    private static readonly HashSet<string> WeakCipherFactories = new(StringComparer.Ordinal) { "DES.Create", "TripleDES.Create", "RC2.Create" };

    private readonly List<PatternFact> _facts = [];
    private readonly HashSet<(string, int)> _seen = [];

    public static IReadOnlyList<PatternFact> Collect(SyntaxTree tree, string filePath, CancellationToken cancellationToken)
    {
        var walker = new SecurityPatternWalker(filePath);
        walker.Visit(tree.GetRoot(cancellationToken));
        return walker._facts;
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var typeName = SimpleName(node.Type);

        if (typeName == "BinaryFormatter")
        {
            Report(SecurityPatternIds.BinaryFormatter, node, "new BinaryFormatter()");
        }
        else if (WeakHashTypes.Contains(typeName))
        {
            Report(SecurityPatternIds.WeakHash, node, $"new {typeName}()");
        }
        else if (WeakCipherTypes.Contains(typeName))
        {
            Report(SecurityPatternIds.WeakSymmetricCipher, node, $"new {typeName}()");
        }
        else if (typeName == "XmlUrlResolver")
        {
            Report(SecurityPatternIds.XmlDtdProcessing, node, "new XmlUrlResolver() (external entity resolution)");
        }
        else if (SqlCommandTypes.Contains(typeName)
                 && node.ArgumentList?.Arguments.FirstOrDefault()?.Expression is { } commandText
                 && IsDynamicString(commandText))
        {
            Report(SecurityPatternIds.SqlStringConcatenation, node, $"new {typeName}(<dynamic SQL>)");
        }

        base.VisitObjectCreationExpression(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var callee = node.Expression.ToString();

        if (WeakHashFactories.Contains(callee))
        {
            Report(SecurityPatternIds.WeakHash, node, $"{callee}()");
        }
        else if (WeakCipherFactories.Contains(callee))
        {
            Report(SecurityPatternIds.WeakSymmetricCipher, node, $"{callee}()");
        }
        else if (callee is "Process.Start" or "System.Diagnostics.Process.Start"
                 && node.ArgumentList.Arguments.Any(a => IsDynamicString(a.Expression)))
        {
            Report(SecurityPatternIds.ProcessStartConcatenation, node, "Process.Start(<dynamic command>)");
        }

        base.VisitInvocationExpression(node);
    }

    public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var target = node.Left.ToString();

        if (target.EndsWith("CommandText", StringComparison.Ordinal) && IsDynamicString(node.Right))
        {
            Report(SecurityPatternIds.SqlStringConcatenation, node, "CommandText = <dynamic SQL>");
        }
        else if (target.EndsWith("ServerCertificateValidationCallback", StringComparison.Ordinal)
                 || target.EndsWith("ServerCertificateCustomValidationCallback", StringComparison.Ordinal))
        {
            Report(SecurityPatternIds.CertificateValidationDisabled, node, $"{LastSegment(target)} assigned");
        }
        else if (target.EndsWith("DtdProcessing", StringComparison.Ordinal)
                 && node.Right.ToString().EndsWith("Parse", StringComparison.Ordinal))
        {
            Report(SecurityPatternIds.XmlDtdProcessing, node, "DtdProcessing = DtdProcessing.Parse");
        }

        base.VisitAssignmentExpression(node);
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var text = node.ToString();

        if (text is "TypeNameHandling.All" or "TypeNameHandling.Auto" or "TypeNameHandling.Objects" or "TypeNameHandling.Arrays")
        {
            Report(SecurityPatternIds.TypeNameHandling, node, text);
        }
        else if (text is "SecurityProtocolType.Ssl3" or "SecurityProtocolType.Tls" or "SecurityProtocolType.Tls11")
        {
            Report(SecurityPatternIds.LegacyTlsProtocol, node, text);
        }

        base.VisitMemberAccessExpression(node);
    }

    public override void VisitAttribute(AttributeSyntax node)
    {
        if (SimpleName(node.Name) is "ValidateInput" or "ValidateInputAttribute"
            && node.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.FalseLiteralExpression))
        {
            Report(SecurityPatternIds.RequestValidationDisabled, node, "[ValidateInput(false)]");
        }

        base.VisitAttribute(node);
    }

    /// <summary>String built from non-literal parts: concatenation, interpolation with holes, or string.Format.</summary>
    private static bool IsDynamicString(ExpressionSyntax expression) => expression switch
    {
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } binary =>
            ContainsNonLiteral(binary),
        InterpolatedStringExpressionSyntax interpolated =>
            interpolated.Contents.OfType<InterpolationSyntax>().Any(),
        InvocationExpressionSyntax invocation =>
            invocation.Expression.ToString() is "string.Format" or "String.Format" or "string.Concat" or "String.Concat",
        ParenthesizedExpressionSyntax parenthesized => IsDynamicString(parenthesized.Expression),
        _ => false,
    };

    private static bool ContainsNonLiteral(BinaryExpressionSyntax binary)
    {
        foreach (var operand in new[] { binary.Left, binary.Right })
        {
            switch (operand)
            {
                case LiteralExpressionSyntax:
                    continue;
                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } nested:
                    if (ContainsNonLiteral(nested))
                    {
                        return true;
                    }

                    continue;
                default:
                    return true;
            }
        }

        return false;
    }

    private void Report(string patternId, SyntaxNode node, string detail)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (!_seen.Add((patternId, line)))
        {
            return;
        }

        _facts.Add(new PatternFact(patternId, filePath, line, EnclosingMember(node), detail));
    }

    private static string EnclosingMember(SyntaxNode node)
    {
        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m is not BaseTypeDeclarationSyntax);
        var type = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();

        var memberName = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            PropertyDeclarationSyntax p => p.Identifier.Text,
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "<field>",
            _ => "<member>",
        };

        return type is null ? memberName : $"{type.Identifier.Text}.{memberName}";
    }

    private static string SimpleName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => type.ToString(),
    };

    private static string SimpleName(NameSyntax name) => name switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => name.ToString(),
    };

    private static string LastSegment(string dotted) => dotted.Split('.')[^1];
}
