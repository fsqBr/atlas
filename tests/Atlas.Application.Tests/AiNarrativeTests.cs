using Atlas.Application.Ai;
using Atlas.Domain.Ai;

namespace Atlas.Application.Tests;

public class AiNarrativeTests
{
    [Fact]
    public void Estimate_scales_with_methods_and_batches()
    {
        var e = AiNarrativeService.Estimate(40, BusinessRuleExtractor.SnippetsPerBatch);

        Assert.Equal(40, e.Methods);
        Assert.Equal(10, e.Requests);
        Assert.Equal(40L * AiNarrativeService.TokensPerSnippetIn + 10L * AiNarrativeService.TokensPerRequestOverhead, e.InputTokens);
        Assert.Equal(40L * AiNarrativeService.TokensPerSnippetOut, e.OutputTokens);

        var none = AiNarrativeService.Estimate(0, 4);
        Assert.Equal(0, none.Requests);
        Assert.Equal(0, none.InputTokens);

        Assert.Equal(1, AiNarrativeService.Estimate(1, 4).Requests);
        Assert.Equal(2, AiNarrativeService.Estimate(5, 4).Requests);
    }

    [Fact]
    public void Narratives_normalize_language_cap_text_and_reject_unknown_kinds()
    {
        var n = new AiNarrative(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AiNarrative.Kinds.ExecutiveSummary, "summary", "pt", new string('a', AiNarrative.MaxTextLength + 500), "m", 10, 20);
        Assert.Equal("pt-BR", n.Lang);
        Assert.Equal(AiNarrative.MaxTextLength, n.Text.Length);
        Assert.Equal("en", AiNarrative.NormalizeLang(null));
        Assert.Equal("en", AiNarrative.NormalizeLang("fr"));

        n.Replace("new", "m2", 1, 2);
        Assert.Equal("new", n.Text);
        Assert.Equal("m2", n.Model);

        Assert.Throws<ArgumentException>(() => new AiNarrative(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "poem", "k", "en", "t", "m", 0, 0));
        Assert.Equal("plan", new AiNarrative(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AiNarrative.Kinds.MigrationPlan, "plan", "en", "# Plan", "m", 0, 0).Key);
        Assert.Throws<ArgumentException>(() => new AiNarrative(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), AiNarrative.Kinds.FindingExplanation, "k", "en", "  ", "m", 0, 0));
    }
}
