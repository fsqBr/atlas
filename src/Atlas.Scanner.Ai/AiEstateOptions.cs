namespace Atlas.Scanner.Ai;

/// <summary>Operator configuration for the AI Estate scanner (section Atlas:AiEstate). Everything is optional.</summary>
public sealed class AiEstateOptions
{
    public const string SectionName = "Atlas:AiEstate";

    /// <summary>
    /// Provider ids the organization has approved (e.g. "azure-openai", "anthropic"). When non-empty, every
    /// external provider found in a repository that is not listed raises <c>ai.unapproved-provider</c>.
    /// Empty/null = no allowlist configured: the rule never fires (the V0.5 "policy engine" is this list).
    /// </summary>
    public List<string> ApprovedProviders { get; set; } = [];

    /// <summary>
    /// Path to a signature catalog JSON that replaces the builtin catalog wholesale (precedence:
    /// custom file > builtin). Invalid or unreadable file → the builtin catalog is used and a warning logged.
    /// </summary>
    public string? CatalogPath { get; set; }

    /// <summary>Base64 HMAC key for fingerprints of secrets found in MCP configs; shared with the secrets scanner (Atlas:Secrets:HmacKeyBase64).</summary>
    public string? HmacKeyBase64 { get; set; }

    public bool HasAllowlist => ApprovedProviders is { Count: > 0 };
}
