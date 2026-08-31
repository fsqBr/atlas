namespace Atlas.Domain.Rules;

/// <summary>
/// Human text for a rule in one language. Findings are stored as data (rule id,
/// severity, evidence, structured values); the words a reader sees are produced
/// from these templates at display time, so the same finding reads correctly in
/// every language. Placeholders: {key} from the occurrence's data, plus the
/// built-ins {file}, {fileName}, {line}, {symbol}.
/// </summary>
public sealed record RuleLocalization(
    string Title,
    string Description,
    string? Remediation = null,
    string? TitleTemplate = null,
    string? MessageTemplate = null);
