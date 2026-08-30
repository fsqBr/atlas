using Atlas.Api;
using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;
using Atlas.Worker;

namespace Atlas.Application.Tests;

public class FindingViewsAndSchedulingTests
{
    [Fact]
    public void Heatmap_groups_by_folder_prefix_and_ranks_by_weighted_severity()
    {
        var rows = FindingViewsBuilder.Heatmap(
        [
            ("src/Web/Controllers/A.cs", Severity.High),
            ("src/Web/Controllers/B.cs", Severity.Low),
            ("src/Web/Views/Index.cshtml", Severity.Medium),
            ("src/Core/Domain/Order.cs", Severity.Critical),
            ("README.md", Severity.Informational),
            (null, Severity.Low),
        ]);

        Assert.Equal(["src/Core", "src/Web", "(no file)", "(root)"], rows.Select(r => r.Folder));
        var web = rows.Single(r => r.Folder == "src/Web");
        Assert.Equal((3, 0, 1, 1, 1, 3), (web.Open, web.Critical, web.High, web.Medium, web.Low, web.Files));
        Assert.Equal(1, rows.Single(r => r.Folder == "src/Core").Critical);
    }

    [Fact]
    public void Scheduled_runs_are_due_only_for_idle_assessments_past_their_cadence()
    {
        var assessment = new Assessment(Guid.NewGuid(), WellKnownTenants.DefaultId, "A", new SourceReference("local", "/x"));
        var now = DateTimeOffset.UtcNow;

        Assert.False(ScheduledRunsService.IsDue(assessment, now)); // no cadence
        assessment.SetSchedule(7, null);
        Assert.False(ScheduledRunsService.IsDue(assessment, now)); // Created: never ran

        assessment.Start();
        assessment.Complete(withWarnings: false);
        Assert.False(ScheduledRunsService.IsDue(assessment, now)); // just completed
        Assert.True(ScheduledRunsService.IsDue(assessment, now.AddDays(7.5)));

        assessment.SetSchedule(null, "https://hooks.example.com/atlas");
        Assert.False(ScheduledRunsService.IsDue(assessment, now.AddDays(30)));
        Assert.Equal("https://hooks.example.com/atlas", assessment.WebhookUrl);
        Assert.Throws<ArgumentException>(() => assessment.SetSchedule(1, "ftp://nope"));
    }

    [Fact]
    public void Webhook_payload_and_signature_are_deterministic()
    {
        var current = new RunSummary(Guid.NewGuid(), 3, "abc", "Completed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 72, 40, 2, 30, 5, 1, 7, 0);
        var previous = current with { Number = 2, HealthScore = 65 };
        var id = Guid.NewGuid();

        var payload = RunNotifier.BuildPayload("Billing", id, current, previous, "http://atlas:3000/");

        Assert.Equal("run.completed", payload.Event);
        Assert.Equal(7, payload.HealthDelta);
        Assert.Equal($"http://atlas:3000/assessments/{id}", payload.Url);
        Assert.Equal(RunNotifier.Sign("body", "s3cret"), RunNotifier.Sign("body", "s3cret"));
        Assert.NotEqual(RunNotifier.Sign("body", "s3cret"), RunNotifier.Sign("body", "other"));
        Assert.Equal(64, RunNotifier.Sign("x", "k").Length);
    }
}
