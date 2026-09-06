using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Microsoft.CodeAnalysis.CSharp;

namespace Atlas.Language.Tests;

/// <summary>Regressions for the 2026-08 rule audit: walker-level false positives/negatives that were fixed.</summary>
public class RuleAuditWalkerTests
{
    private static IReadOnlyList<PatternFact> Sensitive(string source) =>
        SensitiveDataWalker.Collect(CSharpSyntaxTree.ParseText(source), "src/File.cs", CancellationToken.None);

    private static IReadOnlyList<PatternFact> Security(string source) =>
        SecurityPatternWalker.Collect(CSharpSyntaxTree.ParseText(source), "src/File.cs", CancellationToken.None);

    private static IReadOnlyList<PatternFact> Legacy(string source) =>
        LegacyApiWalker.Collect(CSharpSyntaxTree.ParseText(source), "src/File.cs", CancellationToken.None);

    [Fact]
    public void Nameof_arguments_are_names_not_leaked_data()
    {
        var facts = Sensitive("""
            using System;
            public class Guarded
            {
                public void Register(string email, string senha)
                {
                    if (email is null) throw new ArgumentNullException(nameof(email));
                    if (senha is null) throw new ArgumentNullException(nameof(senha));
                    throw new InvalidOperationException($"could not register {email}");
                }
            }
            """);

        // The interpolated email is a real leak; the nameof() guards are not.
        Assert.Single(facts, f => f.PatternId == PrivacyPatternIds.LeakToException);
    }

    [Fact]
    public void Dialog_and_catalog_are_not_log_sinks()
    {
        var facts = Sensitive("""
            public class Screen
            {
                public void Show(Painter dialog, Painter catalogWriter, Logger _logger, string cpf)
                {
                    dialog.LogError(cpf);
                    catalogWriter.LogError(cpf);
                    _logger.LogError(cpf);
                }
            }
            """);

        Assert.Single(facts, f => f.PatternId == PrivacyPatternIds.LeakToLog);
    }

    [Fact]
    public void Service_fields_and_booleans_are_not_pii_inventory()
    {
        var facts = Sensitive("""
            public class CustomerService
            {
                private readonly IEmailService _emailService;
                private readonly EmailValidator emailValidator;
                public bool EmailConfirmed { get; set; }
                public string EmailAddress { get; set; }
            }
            """);

        var fields = facts.Where(f => f.PatternId.StartsWith("pii.field.", StringComparison.Ordinal)).ToList();
        Assert.Single(fields);
        Assert.Contains("EmailAddress", fields[0].Detail);
    }

    [Fact]
    public void Qualified_weak_hash_calls_and_hashdata_are_detected()
    {
        var facts = Security("""
            public class Hashing
            {
                public byte[] A(byte[] data) => System.Security.Cryptography.MD5.HashData(data);
                public object B() => System.Security.Cryptography.MD5.Create();
            }
            """);

        Assert.Equal(2, facts.Count(f => f.PatternId == SecurityPatternIds.WeakHash));
    }

    [Fact]
    public void Target_typed_new_binaryformatter_is_detected()
    {
        var facts = Security("""
            using System.Runtime.Serialization.Formatters.Binary;
            public class Serializer
            {
                public void Run()
                {
                    BinaryFormatter formatter = new();
                }
            }
            """);

        Assert.Single(facts, f => f.PatternId == SecurityPatternIds.BinaryFormatter);
    }

    [Fact]
    public void Modern_smtp_and_oracle_types_are_not_legacy_apis()
    {
        var modern = Legacy("""
            using MailKit.Net.Smtp;
            using Oracle.ManagedDataAccess.Client;
            public class Mailer
            {
                public void Send()
                {
                    var client = new SmtpClient();
                    var connection = new OracleConnection();
                }
            }
            """);
        Assert.Empty(modern);

        var legacy = Legacy("""
            using System.Net.Mail;
            public class Mailer
            {
                public void Send()
                {
                    var client = new SmtpClient();
                }
            }
            """);
        Assert.Single(legacy);

        var qualified = Legacy("""
            public class Mailer
            {
                public void Send()
                {
                    var client = new System.Net.Mail.SmtpClient();
                }
            }
            """);
        Assert.Single(qualified);
    }

    [Fact]
    public void Pattern_combinators_count_like_their_boolean_equivalents()
    {
        var analyzer = new CSharpLanguageAnalyzer();
        const string patterns = """
            public class C
            {
                public bool IsWeekend(DayOfWeek d) => d is DayOfWeek.Saturday or DayOfWeek.Sunday;
                public bool IsWeekendOld(DayOfWeek d) => d == DayOfWeek.Saturday || d == DayOfWeek.Sunday;
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(patterns);
        var root = tree.GetRoot();
        var methods = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().ToList();

        // Both spellings of the same decision must score identically.
        Assert.Equal(
            Complexity(methods[1]),
            Complexity(methods[0]));

        static int Complexity(Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax method)
        {
            var type = typeof(CSharpLanguageAnalyzer).Assembly.GetType("Atlas.Language.CSharp.CyclomaticComplexityWalker")!;
            return (int)type.GetMethod("Measure", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [method])!;
        }
    }
}
