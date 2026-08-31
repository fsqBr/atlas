using Atlas.Application.Ai;
using Atlas.Domain.Ai;

namespace Atlas.Application.Tests;

public class AiFeedbackTests
{
    private static AiNarrative Narrative(string kind, string model, int? rating = null, string? comment = null)
    {
        var n = new AiNarrative(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), kind, "k", "en", "text", model, 1, 1);
        if (rating is not null)
        {
            n.Rate(rating.Value, comment, "tester");
        }

        return n;
    }

    private static BusinessRule Rule(string model, int? rating = null)
    {
        var r = new BusinessRule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "a.cs", "A.B", 1, "Rule", "en", "pt", BusinessRuleCategory.Validation, "[]", 0.8, model);
        if (rating is not null)
        {
            r.Rate(rating.Value, null, "tester");
        }

        return r;
    }

    [Fact]
    public void Rating_is_one_of_minus_one_zero_one_and_zero_clears()
    {
        var n = Narrative(AiNarrative.Kinds.FindingFix, "m");
        n.Rate(-1, "  too generic  ", "ana");
        Assert.Equal(-1, n.Rating);
        Assert.Equal("too generic", n.FeedbackComment);
        Assert.Equal("ana", n.RatedBy);
        Assert.NotNull(n.RatedAtUtc);

        n.Rate(0, null, "ana");
        Assert.Null(n.Rating);
        Assert.Null(n.FeedbackComment);
        Assert.Null(n.RatedAtUtc);

        Assert.Throws<ArgumentOutOfRangeException>(() => n.Rate(2, null, null));
        n.Rate(1, new string('x', 900), null);
        Assert.Equal(500, n.FeedbackComment!.Length);

        var r = Rule("m");
        r.Rate(1, null, "bob");
        Assert.Equal(1, r.Rating);
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Rate(-2, null, null));
    }

    [Fact]
    public void Summary_aggregates_by_kind_and_model_and_lists_recent_votes()
    {
        var narratives = new List<AiNarrative>
        {
            Narrative(AiNarrative.Kinds.FindingExplanation, "claude", 1),
            Narrative(AiNarrative.Kinds.FindingExplanation, "claude", 1),
            Narrative(AiNarrative.Kinds.FindingExplanation, "gpt", -1, "missed the point"),
            Narrative(AiNarrative.Kinds.MigrationPlan, "claude", -1, "too long"),
            Narrative(AiNarrative.Kinds.ExecutiveSummary, "claude"), // unrated
        };
        var rules = new List<BusinessRule> { Rule("claude", 1), Rule("claude", -1), Rule("gpt") };

        var s = AiFeedbackSummary.From(narratives, rules, recent: 3);

        Assert.Equal(3, s.Up);
        Assert.Equal(3, s.Down);
        var explanation = s.ByKind.Single(b => b.Key == AiNarrative.Kinds.FindingExplanation);
        Assert.Equal(2, explanation.Up);
        Assert.Equal(1, explanation.Down);
        Assert.Equal(0.667, explanation.HelpfulShare);
        Assert.Equal(1, s.ByKind.Single(b => b.Key == AiFeedbackSummary.BusinessRuleKind).Down);
        Assert.DoesNotContain(s.ByKind, b => b.Key == AiNarrative.Kinds.ExecutiveSummary);
        Assert.Equal(new[] { "claude", "gpt" }, s.ByModel.Select(b => b.Key));
        Assert.Equal(5, s.ByModel[0].Up + s.ByModel[0].Down); // claude: 2 explanations up, 1 plan down, 1 rule up, 1 rule down
        Assert.Equal(3, s.Recent.Count);
        Assert.Contains(s.Recent, e => e.Comment == "too long" && e.Title == "Migration plan");
        Assert.Null(new FeedbackBucket("x", 0, 0).HelpfulShare);
    }

    [Fact]
    public void Ratable_kinds_exclude_pr_notes()
    {
        Assert.Contains(AiNarrative.Kinds.FindingFix, AiFeedbackService.RatableKinds);
        Assert.Contains(AiNarrative.Kinds.MigrationPlan, AiFeedbackService.RatableKinds);
        Assert.DoesNotContain(AiNarrative.Kinds.PrSummary, AiFeedbackService.RatableKinds);
    }
}
