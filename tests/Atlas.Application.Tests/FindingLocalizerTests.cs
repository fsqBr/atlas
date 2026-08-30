using Atlas.Application.Findings;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;

namespace Atlas.Application.Tests;

public class FindingLocalizerTests
{
    private static readonly RuleDefinition Rule = new(
        "dependency.framework.end-of-life", "dep", "1.0.0", FindingCategory.Modernization, Severity.High,
        "Target framework out of support", "desc", "Retarget to a supported framework.",
        FindingLocalizer.Serialize(new Dictionary<string, RuleLocalization>
        {
            ["pt-BR"] = new("Framework alvo fora de suporte", "descrição", "Mude para um framework suportado.",
                "{framework} {version} — fora de suporte", "{framework} {version} deixou de receber suporte em {endOfLife} ({fileName}, linha {line})."),
        }));

    private static readonly Finding Finding = Finding.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "fp", Rule.Id, FindingCategory.Modernization, Severity.High,
        ".NET Framework 4.5 — EndOfLife", FindingOrigin.Deterministic, Guid.NewGuid());

    private static readonly FindingOccurrence Occurrence = new(
        Guid.NewGuid(), Finding.TenantId, Finding.Id, Guid.NewGuid(), Severity.High, ConfidenceLevel.High,
        ".NET Framework 4.5 left support on 2016-01-12.", null,
        new Evidence("dep", "0.1.0", "src/App/App.csproj", 7, null, "v4.5"),
        """{"framework":".NET Framework","version":"4.5","endOfLife":"2016-01-12"}""");

    [Fact]
    public void English_returns_stored_text_untouched()
    {
        var text = FindingLocalizer.Localize(Finding, Occurrence, Rule, "en");

        Assert.Equal(".NET Framework 4.5 — EndOfLife", text.Title);
        Assert.Equal(".NET Framework 4.5 left support on 2016-01-12.", text.Message);
        Assert.Equal("Retarget to a supported framework.", text.Remediation);
    }

    [Fact]
    public void Portuguese_renders_templates_from_structured_data_and_evidence()
    {
        var text = FindingLocalizer.Localize(Finding, Occurrence, Rule, "pt-BR");

        Assert.Equal(".NET Framework 4.5 — fora de suporte", text.Title);
        Assert.Equal(".NET Framework 4.5 deixou de receber suporte em 2016-01-12 (App.csproj, linha 7).", text.Message);
        Assert.Equal("Mude para um framework suportado.", text.Remediation);
    }

    [Fact]
    public void Language_prefix_matches_and_unknown_language_falls_back_to_english()
    {
        Assert.Equal("Framework alvo fora de suporte", FindingLocalizer.RuleTitle(Rule, Rule.Id, "pt"));
        Assert.Equal("Target framework out of support", FindingLocalizer.RuleTitle(Rule, Rule.Id, "de"));
        Assert.Equal("Target framework out of support", FindingLocalizer.RuleTitle(Rule, Rule.Id, null));
        Assert.Equal("rule.unknown", FindingLocalizer.RuleTitle(null, "rule.unknown", "pt-BR"));
    }

    [Fact]
    public void Missing_template_falls_back_to_localized_title_and_stored_message()
    {
        var rule = new RuleDefinition("r", "s", "1.0.0", FindingCategory.Quality, Severity.Low, "Large file", "d", null,
            FindingLocalizer.Serialize(new Dictionary<string, RuleLocalization> { ["pt-BR"] = new("Arquivo grande", "d") }));

        var text = FindingLocalizer.Localize(Finding, Occurrence, rule, "pt-BR");

        Assert.Equal("Arquivo grande", text.Title);
        Assert.Equal(Occurrence.Message, text.Message);
    }

    [Fact]
    public void Unknown_placeholders_render_empty_and_never_leak_braces()
    {
        var rule = new RuleDefinition("r", "s", "1.0.0", FindingCategory.Quality, Severity.Low, "t", "d", null,
            FindingLocalizer.Serialize(new Dictionary<string, RuleLocalization> { ["pt-BR"] = new("t", "d", null, "X {nope} Y", null) }));

        Assert.Equal("X  Y", FindingLocalizer.Localize(Finding, Occurrence, rule, "pt-BR").Title);
    }
}
