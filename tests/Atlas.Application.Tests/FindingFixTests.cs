using Atlas.Application.Ai;
using Atlas.Domain.Findings;
using Atlas.Domain.Jobs;

namespace Atlas.Application.Tests;

/// <summary>Guards around "suggest a fix": what may be sent, what gets redacted, how the snippet is cut, and what the model is asked.</summary>
public class FindingFixTests
{
    [Fact]
    public void Eligibility_refuses_secrets_binaries_and_locationless_findings()
    {
        Assert.Null(FindingFixEligibility.Reject("sec.sql.string-concatenation", FindingCategory.Security, "src/Repo.cs"));
        Assert.Contains("never sent", FindingFixEligibility.Reject("secrets.connection-string-password", FindingCategory.Security, "Web.config"));
        Assert.Contains("never sent", FindingFixEligibility.Reject("custom.rule", FindingCategory.Secrets, "appsettings.json"));
        Assert.Contains("binary", FindingFixEligibility.Reject("dependency.package.vulnerable", FindingCategory.Dependencies, "lib/Old.dll"));
        Assert.Contains("no file location", FindingFixEligibility.Reject("quality.tests.none", FindingCategory.Quality, null));
        Assert.Contains("no file location", FindingFixEligibility.Reject("quality.tests.none", FindingCategory.Quality, "estate"));
    }

    [Fact]
    public void Redactor_masks_credential_values_but_keeps_the_code_shape()
    {
        const string code = """
            var cs = "Server=db;Database=shop;User Id=sa;Password=Sup3rS3cret!;";
            var key = configuration["ApiKey"];
            client.DefaultRequestHeaders.Add("Authorization", "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.abc");
            var aws = "AKIAIOSFODNN7EXAMPLE";
            var pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEow...\n-----END RSA PRIVATE KEY-----";
            var token = "sk-live-abcdefghijklmnopqrstuvwxyz";
            """;

        var redacted = SnippetRedactor.Redact(code, out var count);

        Assert.DoesNotContain("Sup3rS3cret!", redacted);
        Assert.Contains("Password=***", redacted);
        Assert.Contains("Bearer ***", redacted);
        Assert.Contains("AKIA***", redacted);
        Assert.DoesNotContain("MIIEow", redacted);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", redacted); // the token value goes, whichever pattern catches it first
        Assert.Contains("configuration[\"ApiKey\"]", redacted); // a key *name* without a value is untouched
        Assert.True(count >= 5, $"expected at least five redactions, got {count}");
    }

    [Fact]
    public void Snippet_keeps_the_flagged_lines_centred_within_the_caps()
    {
        var content = string.Join("\n", Enumerable.Range(1, 400).Select(i => $"line {i};"));

        var around = FixSnippets.Extract("src/A.cs", content, 200, 202);
        Assert.Equal(175, around.FirstLine);
        Assert.Equal(227, around.LastLine);
        Assert.Equal(400, around.TotalLines);
        Assert.StartsWith("175: line 175;", around.Text);
        Assert.Contains("200: line 200;", around.Text);
        Assert.False(around.Truncated);

        var top = FixSnippets.Extract("src/A.cs", content, 3, null);
        Assert.Equal(1, top.FirstLine);
        Assert.Equal(28, top.LastLine);

        var wide = FixSnippets.Extract("src/A.cs", content, 100, 300); // flagged range wider than the cap
        Assert.True(wide.LastLine - wide.FirstLine + 1 <= FixSnippets.MaxLines);
        Assert.Equal(100, wide.FirstLine);

        var longLines = string.Join("\n", Enumerable.Range(1, 60).Select(i => new string('x', 400)));
        var capped = FixSnippets.Extract("src/B.cs", longLines, 30, 30);
        Assert.True(capped.Truncated);
        Assert.True(capped.Text.Length <= FixSnippets.MaxChars + 8);
    }

    [Fact]
    public void Prompt_carries_rule_finding_snippet_and_the_fixed_answer_structure()
    {
        var snippet = FixSnippets.Extract("src/Repo.cs", "a\nvar sql = \"SELECT * FROM t WHERE id = \" + id;\nc", 2, 2);
        var en = FindingFixRunner.BuildPrompt("en", "sec.sql.string-concatenation", "SQL built from strings", "Concatenated SQL", "Query built with +", "Use parameters", snippet, 2, 2, 1);

        Assert.Contains("Rule: sec.sql.string-concatenation — SQL built from strings", en);
        Assert.Contains("File: src/Repo.cs (line 2 flagged; the snippet shows lines 1–3 of 3)", en);
        Assert.Contains("1 credential value(s) in the snippet were replaced by ***", en);
        Assert.Contains("2: var sql = ", en);
        Assert.Contains("## Diagnosis", en);
        Assert.Contains("--- a/src/Repo.cs", en);
        Assert.Contains("## Notes", en);

        var pt = FindingFixRunner.BuildPrompt("pt-BR", "r", "t", null, "m", null, snippet, null, null, 0);
        Assert.Contains("## Diagnóstico", pt);
        Assert.Contains("unknown line flagged", pt);
        Assert.DoesNotContain("credential value", pt);
    }

    [Fact]
    public void Fix_job_carries_its_payload_and_parses_back()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new FindingFixRequest(Guid.Parse("22222222-2222-2222-2222-222222222222"), "pt-BR"));
        var job = new ScanJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ScanJob.Kinds.FindingFix, payload);

        Assert.Equal("ai.fix", job.Kind);
        Assert.Equal(payload, job.Payload);
        var parsed = FindingFixRunner.Parse(job.Payload);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), parsed!.FindingId);
        Assert.Equal("pt-BR", parsed.Lang);
        Assert.Null(FindingFixRunner.Parse("not json"));
        Assert.Null(FindingFixRunner.Parse(null));
        Assert.Throws<ArgumentException>(() => new ScanJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "ai.unknown"));
        Assert.Throws<ArgumentException>(() => new ScanJob(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ScanJob.Kinds.Scan, new string('p', 2001)));
    }
}
