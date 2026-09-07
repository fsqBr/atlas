using Atlas.Domain.Findings;

namespace Atlas.Domain.Tests.Findings;

public class FindingFingerprintTests
{
    [Fact]
    public void Same_inputs_same_fingerprint()
    {
        var a = FindingFingerprint.Compute("dep.eol", 1, "github.com/acme/billing", "src/App/App.csproj", "net45");
        var b = FindingFingerprint.Compute("dep.eol", 1, "github.com/acme/billing", "src/App/App.csproj", "net45");

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Path_separators_case_and_leading_dot_do_not_matter()
    {
        var unix = FindingFingerprint.Compute("r", 1, "repo", "src/App/App.csproj", "s");
        var windows = FindingFingerprint.Compute("r", 1, "repo", @".\SRC\App\app.csproj", "s");

        Assert.Equal(unix, windows);
    }

    [Fact]
    public void Repository_key_and_rule_id_are_case_insensitive()
    {
        var a = FindingFingerprint.Compute("Dep.EOL", 1, "GitHub.com/Acme/Billing", "p", "s");
        var b = FindingFingerprint.Compute("dep.eol", 1, "github.com/acme/billing", "p", "s");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Rule_major_version_symbol_and_repository_change_the_fingerprint()
    {
        var baseline = FindingFingerprint.Compute("r", 1, "repo", "p", "s");

        Assert.NotEqual(baseline, FindingFingerprint.Compute("r", 2, "repo", "p", "s"));
        Assert.NotEqual(baseline, FindingFingerprint.Compute("r", 1, "repo", "p", "other"));
        Assert.NotEqual(baseline, FindingFingerprint.Compute("r", 1, "other-repo", "p", "s"));
        Assert.NotEqual(baseline, FindingFingerprint.Compute("r", 1, "repo", "q", "s"));
    }

    [Theory]
    [InlineData("1.0.0", 1)]
    [InlineData("v2.3", 2)]
    [InlineData("10", 10)]
    [InlineData("garbage", 0)]
    public void Major_version_is_parsed_leniently(string version, int expected)
    {
        Assert.Equal(expected, FindingFingerprint.MajorVersionOf(version));
    }
}
