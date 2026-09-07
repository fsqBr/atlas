using Atlas.Language.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atlas.Language.CSharp;

/// <summary>
/// Syntactic detection of APIs that are gone, obsolete or discouraged on modern
/// .NET (.8 "obsolete APIs"). Name-based, so it works at Tier 1;
/// the semantic pass adds [Obsolete]-attributed symbols on top.
/// </summary>
public sealed class LegacyApiWalker(string filePath) : CSharpSyntaxWalker
{
    /// <summary>Member-access / invocation callee text → why it matters.</summary>
    private static readonly IReadOnlyDictionary<string, string> LegacyCallees = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WebRequest.Create"] = "WebRequest is obsolete on modern .NET; use HttpClient",
        ["HttpWebRequest.Create"] = "HttpWebRequest is obsolete on modern .NET; use HttpClient",
        ["Thread.Abort"] = "Thread.Abort throws PlatformNotSupportedException on modern .NET; use CancellationToken",
        ["Thread.Suspend"] = "Thread.Suspend is not supported on modern .NET",
        ["Thread.Resume"] = "Thread.Resume is not supported on modern .NET",
        ["AppDomain.CreateDomain"] = "Secondary AppDomains are not supported on modern .NET; use AssemblyLoadContext",
        ["AppDomain.Unload"] = "AppDomain unloading is not supported on modern .NET; use AssemblyLoadContext",
        ["Assembly.LoadWithPartialName"] = "Assembly.LoadWithPartialName is obsolete",
        ["HttpContext.Current"] = "HttpContext.Current (System.Web) does not exist in ASP.NET Core; inject IHttpContextAccessor",
        ["HttpRuntime.Cache"] = "HttpRuntime.Cache (System.Web) does not exist in ASP.NET Core; use IMemoryCache",
        ["ConfigurationManager.AppSettings"] = "System.Configuration app settings; move to IConfiguration",
        ["ConfigurationManager.ConnectionStrings"] = "System.Configuration connection strings; move to IConfiguration",
        ["Marshal.GetExceptionCode"] = "Marshal.GetExceptionCode is obsolete",
        ["SmtpClient.Send"] = "System.Net.Mail.SmtpClient is discouraged (not recommended for new development); use MailKit",
    };

    private static readonly IReadOnlyDictionary<string, string> LegacyTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WebClient"] = "WebClient is obsolete on modern .NET; use HttpClient",
        ["HttpWebRequest"] = "HttpWebRequest is obsolete on modern .NET; use HttpClient",
        ["SmtpClient"] = "System.Net.Mail.SmtpClient is discouraged; use MailKit",
        ["SoapFormatter"] = "SoapFormatter is not available on modern .NET",
        ["NetDataContractSerializer"] = "NetDataContractSerializer is not available on modern .NET",
        ["CodeDomProvider"] = "CodeDOM compilation is not supported on modern .NET; use Roslyn",
        ["ServiceHost"] = "WCF ServiceHost is not available on modern .NET; use CoreWCF or gRPC/REST",
        ["MessageQueue"] = "MSMQ (System.Messaging) is not available on modern .NET",
        ["OracleConnection"] = "System.Data.OracleClient is deprecated; use Oracle.ManagedDataAccess",
    };

    /// <summary>Types whose bare name collides with the recommended replacement (MailKit's SmtpClient,
    /// Oracle.ManagedDataAccess's OracleConnection, CoreWCF's ServiceHost): only flagged when the file
    /// imports the legacy namespace or writes it fully qualified — otherwise the walker would tell
    /// already-modern code to migrate to what it already uses.</summary>
    private static readonly IReadOnlyDictionary<string, string> AmbiguousTypeNamespaces = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SmtpClient"] = "System.Net.Mail",
        ["OracleConnection"] = "System.Data.OracleClient",
        ["MessageQueue"] = "System.Messaging",
        ["ServiceHost"] = "System.ServiceModel",
    };

    private readonly List<PatternFact> _facts = [];
    private readonly HashSet<(int, string)> _seen = [];
    private readonly HashSet<string> _usings = new(StringComparer.Ordinal);

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        if (node.Name is not null)
        {
            _usings.Add(node.Name.ToString());
        }

        base.VisitUsingDirective(node);
    }

    private bool LegacyNamespaceInScope(string typeName, string writtenType) =>
        !AmbiguousTypeNamespaces.TryGetValue(typeName, out var ns)
        || writtenType.StartsWith(ns + ".", StringComparison.Ordinal)
        || _usings.Contains(ns);

    public static IReadOnlyList<PatternFact> Collect(SyntaxTree tree, string filePath, CancellationToken cancellationToken)
    {
        var walker = new LegacyApiWalker(filePath);
        walker.Visit(tree.GetRoot(cancellationToken));
        return walker._facts;
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var text = node.ToString();
        var tail = text.Contains('.') ? text[(text.LastIndexOf('.', Math.Max(0, text.LastIndexOf('.') - 1)) + 1)..] : text;
        foreach (var (callee, why) in LegacyCallees)
        {
            if (text == callee || text.EndsWith("." + callee, StringComparison.Ordinal) || tail == callee)
            {
                var calleeType = callee[..callee.IndexOf('.')];
                if (!LegacyNamespaceInScope(calleeType, text))
                {
                    continue;
                }

                Report(node, $"{callee}: {why}");
                break;
            }
        }

        base.VisitMemberAccessExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var typeName = node.Type switch
        {
            QualifiedNameSyntax q => q.Right.ToString(),
            GenericNameSyntax g => g.Identifier.Text,
            _ => node.Type.ToString(),
        };
        if (LegacyTypes.TryGetValue(typeName, out var why) && LegacyNamespaceInScope(typeName, node.Type.ToString()))
        {
            Report(node, $"new {typeName}(): {why}");
        }

        base.VisitObjectCreationExpression(node);
    }

    private void Report(SyntaxNode node, string detail)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (_seen.Add((line, detail)))
        {
            _facts.Add(new PatternFact(QualityPatternIds.LegacyApi, filePath, line, EnclosingMember(node), detail));
        }
    }

    private static string EnclosingMember(SyntaxNode node)
    {
        var type = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "<global>";
        var member = node.Ancestors().Select(a => a switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            _ => null,
        }).FirstOrDefault(n => n is not null);
        return member is null ? type : $"{type}.{member}";
    }
}

/// <summary>Semantic pass: usages of symbols carrying [Obsolete], with the attribute's message when present.</summary>
internal static class ObsoleteApiDetector
{
    private const int MaxPerFile = 50;

    public static IReadOnlyList<PatternFact> Collect(SemanticModel model, string filePath, CancellationToken cancellationToken)
    {
        var facts = new List<PatternFact>();
        var seen = new HashSet<(int, string)>();
        var root = model.SyntaxTree.GetRoot(cancellationToken);

        foreach (var node in root.DescendantNodes().Where(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or MemberAccessExpressionSyntax))
        {
            if (facts.Count >= MaxPerFile)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
            if (symbol is null)
            {
                continue;
            }

            var attribute = symbol.GetAttributes().Concat(symbol.ContainingType?.GetAttributes() ?? [])
                .FirstOrDefault(a => a.AttributeClass?.Name == "ObsoleteAttribute");
            if (attribute is null)
            {
                continue;
            }

            var message = attribute.ConstructorArguments.FirstOrDefault().Value as string;
            var display = symbol.ContainingType is null ? symbol.Name : $"{symbol.ContainingType.Name}.{symbol.Name}";
            var detail = string.IsNullOrWhiteSpace(message) ? $"{display} is [Obsolete]" : $"{display} is [Obsolete]: {message}";
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (seen.Add((line, display)))
            {
                var type = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "<global>";
                var member = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
                facts.Add(new PatternFact(QualityPatternIds.ObsoleteApi, filePath, line, member is null ? type : $"{type}.{member}", detail));
            }
        }

        return facts;
    }
}
