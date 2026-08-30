using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlas.Language.CSharp;

/// <summary>
/// Standard cyclomatic complexity: 1 + decision points. Counted: if, while,
/// for, foreach, case/case-pattern, catch, conditional (?:), &amp;&amp;, ||, ??, ??=.
/// </summary>
internal static class CyclomaticComplexityWalker
{
    public static int Measure(BaseMethodDeclarationSyntax method)
    {
        var complexity = 1;

        foreach (var node in method.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case DoStatementSyntax:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case SwitchExpressionArmSyntax:
                case CatchClauseSyntax:
                case ConditionalExpressionSyntax:
                    complexity++;
                    break;

                case BinaryExpressionSyntax binary when
                    binary.IsKind(SyntaxKind.LogicalAndExpression)
                    || binary.IsKind(SyntaxKind.LogicalOrExpression)
                    || binary.IsKind(SyntaxKind.CoalesceExpression):
                    complexity++;
                    break;

                case AssignmentExpressionSyntax assignment when
                    assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                    complexity++;
                    break;
            }
        }

        return complexity;
    }
}
