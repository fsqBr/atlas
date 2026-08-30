using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Microsoft.CodeAnalysis.CSharp;

namespace Atlas.Language.Tests;

public class SensitiveDataWalkerTests
{
    private const string Source = """
        using System;
        using Microsoft.Extensions.Logging;

        namespace Shop.Domain
        {
            public class Customer
            {
                public string Cpf { get; set; }
                public string EmailAddress { get; set; }
                public string CardNumber { get; set; }
                public string Cvv { get; set; }
                public string Diagnostico { get; set; }
                public string PasswordHash { get; set; }
                public DateTime DataNascimento { get; set; }
                public string Name { get; set; }
                public int OrderCount { get; set; }
                private string _telefone;
            }

            public record Address(string Logradouro, string Cep, string City);

            public class CustomerService
            {
                private readonly ILogger<CustomerService> _logger;

                public void Register(Customer customer, string senha)
                {
                    _logger.LogInformation("Registering {Cpf} with {Email}", customer.Cpf, customer.EmailAddress);
                    Console.WriteLine($"password={senha}");
                    _logger.LogDebug("Order count {Count}", customer.OrderCount);
                    if (customer.Cpf.Length != 11) throw new ArgumentException($"CPF inválido: {customer.Cpf}");
                    var emailSender = new EmailSender();
                    emailSender.Send(customer);
                }
            }
        }
        """;

    private static IReadOnlyList<PatternFact> Facts() =>
        SensitiveDataWalker.Collect(CSharpSyntaxTree.ParseText(Source), "Shop/Customer.cs", CancellationToken.None);

    [Theory]
    [InlineData("Cpf", "identifier")]
    [InlineData("customer_cnpj", "identifier")]
    [InlineData("PassportNumber", "identifier")]
    [InlineData("EmailAddress", "contact")]
    [InlineData("telefoneCelular", "contact")]
    [InlineData("CardNumber", "financial")]
    [InlineData("Cvv", "financial")]
    [InlineData("SalarioBruto", "financial")]
    [InlineData("Diagnostico", "health")]
    [InlineData("BloodType", "health")]
    [InlineData("PasswordHash", "credential")]
    [InlineData("Senha", "credential")]
    [InlineData("DataNascimento", "birth")]
    [InlineData("Dob", "birth")]
    [InlineData("Name", null)]
    [InlineData("OrderCount", null)]
    [InlineData("Cidade", null)]
    [InlineData("Argument", null)] // contains "rg" but not as a token
    [InlineData("Pisos", null)]
    public void Classifies_member_names_by_token(string name, string? expected) => Assert.Equal(expected, SensitiveDataWalker.Classify(name));

    [Fact]
    public void Finds_fields_by_category_with_type_and_member_symbols()
    {
        var facts = Facts();
        var fields = facts.Where(f => f.PatternId.StartsWith("pii.field.")).ToList();

        Assert.Contains(fields, f => f.PatternId == "pii.field.identifier" && f.Symbol == "Customer.Cpf");
        Assert.Contains(fields, f => f.PatternId == "pii.field.contact" && f.Symbol == "Customer.EmailAddress");
        Assert.Contains(fields, f => f.PatternId == "pii.field.contact" && f.Symbol == "Customer._telefone");
        Assert.Contains(fields, f => f.PatternId == "pii.field.financial" && f.Symbol == "Customer.CardNumber");
        Assert.Contains(fields, f => f.PatternId == "pii.field.financial" && f.Symbol == "Customer.Cvv");
        Assert.Contains(fields, f => f.PatternId == "pii.field.health" && f.Symbol == "Customer.Diagnostico");
        Assert.Contains(fields, f => f.PatternId == "pii.field.credential" && f.Symbol == "Customer.PasswordHash");
        Assert.Contains(fields, f => f.PatternId == "pii.field.birth" && f.Symbol == "Customer.DataNascimento" && f.Detail == "DataNascimento : DateTime");
        Assert.Contains(fields, f => f.PatternId == "pii.field.contact" && f.Symbol == "Address.Logradouro");
        Assert.Contains(fields, f => f.PatternId == "pii.field.contact" && f.Symbol == "Address.Cep");
        Assert.DoesNotContain(fields, f => f.Symbol.EndsWith(".Name") || f.Symbol.EndsWith(".OrderCount") || f.Symbol.EndsWith(".City"));
    }

    [Fact]
    public void Finds_leaks_into_logs_and_exceptions_but_not_harmless_logging()
    {
        var facts = Facts();
        var leaks = facts.Where(f => f.PatternId.StartsWith("pii.leak.")).ToList();

        var logLeaks = leaks.Where(l => l.PatternId == PrivacyPatternIds.LeakToLog).ToList();
        Assert.Contains(logLeaks, l => l.Detail == "_logger.LogInformation ← Cpf [identifier]" && l.Symbol == "CustomerService.Register");
        Assert.Contains(logLeaks, l => l.Detail == "_logger.LogInformation ← EmailAddress [contact]");
        Assert.Contains(logLeaks, l => l.Detail == "Console.WriteLine ← senha [credential]");
        Assert.DoesNotContain(logLeaks, l => l.Detail.Contains("OrderCount"));
        Assert.DoesNotContain(leaks, l => l.Detail.Contains("emailSender")); // a variable named like a service, used as a receiver, is not data

        var exceptionLeak = Assert.Single(leaks, l => l.PatternId == PrivacyPatternIds.LeakToException);
        Assert.Equal("new ArgumentException(…) ← Cpf [identifier]", exceptionLeak.Detail);
    }
}
