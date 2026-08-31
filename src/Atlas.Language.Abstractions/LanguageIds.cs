namespace Atlas.Language.Abstractions;

/// <summary>
/// The canonical language-id strings shared between language adapters (which declare them on
/// <see cref="LanguageDescriptor.LanguageId"/>) and scanners (which look up
/// <c>ScanContext.Languages[id]</c>). One home so a typo can't silently mean "no language facts"
/// and adding a language is a single edit, not a grep across scanners.
/// </summary>
public static class LanguageIds
{
    public const string CSharp = "csharp";
    public const string VisualBasic = "vb";
    public const string Sql = "sql";
    public const string Java = "java";
    public const string Python = "python";
}
