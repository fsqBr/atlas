using System.Text.RegularExpressions;

namespace Atlas.Language.Abstractions;

/// <summary>
/// Classifies an identifier (member, column, parameter) into a personal-data
/// category by its name: identifier, contact, financial, health, credential or
/// birth. Shared by language adapters and by scanners that look at schema
/// files, so C# properties and SQL columns are judged by the same vocabulary.
/// </summary>
public static partial class SensitiveNameClassifier
{
    private static readonly IReadOnlyList<(string Category, string[] Tokens, string[] Substrings)> Categories =
    [
        ("identifier", ["cpf", "cnpj", "rg", "ssn", "nis", "pis", "cnh", "passport", "passaporte", "nif", "nie"],
            ["socialsecurity", "nationalid", "taxid", "tituloeleitor", "documentoidentidade"]),
        ("contact", ["email", "phone", "telefone", "celular", "whatsapp", "cep", "zipcode", "postalcode", "logradouro", "endereco"],
            ["emailaddress", "phonenumber", "mobilephone", "streetaddress", "homeaddress", "residentialaddress"]),
        ("financial", ["cvv", "cvc", "iban", "salary", "salario", "renda", "income", "pan"],
            ["creditcard", "cartaocredito", "cartaodecredito", "cardnumber", "numerocartao", "bankaccount", "contabancaria", "contacorrente", "numeroconta"]),
        ("health", ["diagnostico", "diagnosis", "prontuario", "doenca", "disease", "alergia", "allergy", "deficiencia", "disability", "cid"],
            ["medicalrecord", "healthcondition", "bloodtype", "tiposanguineo", "medicalhistory", "historicomedico", "planodesaude", "healthplan"]),
        ("credential", ["password", "senha", "passwd", "pwd"], ["secretanswer", "securityanswer", "passwordhash", "senhahash"]),
        ("birth", ["dob", "nascimento", "birthdate", "birthday"], ["datanascimento", "dateofbirth", "datadenascimento"]),
    ];

    public static IReadOnlyList<string> AllCategories { get; } = Categories.Select(c => c.Category).ToList();

    public static string? Classify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var tokens = Tokenize(name);
        var joined = string.Concat(tokens);
        foreach (var (category, tokenList, substrings) in Categories)
        {
            if (tokens.Any(t => tokenList.Contains(t, StringComparer.Ordinal)) || substrings.Any(s => joined.Contains(s, StringComparison.Ordinal)))
            {
                return category;
            }
        }

        return null;
    }

    private static string[] Tokenize(string name)
    {
        var spaced = CamelBoundary().Replace(name.Replace('_', ' ').Replace('-', ' ').Trim('[', ']', '"', '`'), " ");
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToArray();
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBoundary();
}
