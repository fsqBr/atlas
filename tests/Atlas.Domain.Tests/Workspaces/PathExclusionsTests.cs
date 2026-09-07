using Atlas.Domain.Findings;
using Atlas.Domain.Tenants;
using Atlas.Domain.Workspaces;

namespace Atlas.Domain.Tests.Workspaces;

public class PathExclusionsTests
{
    [Theory]
    [InlineData("vendor/jquery/jquery.js", true)]
    [InlineData("src/vendor/lib.cs", true)]
    [InlineData("src/app/site.min.js", true)]
    [InlineData("src/app/site.js", false)]
    [InlineData("third-party/x/y.cs", true)]
    [InlineData("src/Vendors/x.cs", true)]
    [InlineData("src/Customer.cs", false)]
    public void Defaults_exclude_vendored_and_minified_code(string path, bool excluded) =>
        Assert.Equal(excluded, PathExclusions.Compile(null).IsExcluded(path));

    [Theory]
    [InlineData("legacy-copy/", "legacy-copy/Old.cs", true)]
    [InlineData("legacy-copy/", "src/legacy-copy/Old.cs", true)]
    [InlineData("/spikes/targets/**", "spikes/targets/BlogEngine/x.cs", true)]
    [InlineData("/spikes/targets/**", "src/spikes/targets/x.cs", false)]
    [InlineData("**/*.generated.cs", "a/b/c/Model.generated.cs", true)]
    [InlineData("**/*.generated.cs", "a/b/c/Model.cs", false)]
    [InlineData("*.Designer.cs", "Web/Default.aspx.Designer.cs", true)]
    [InlineData("Migrations", "src/App/Migrations/001.cs", true)]
    [InlineData("src/App/Migrations", "src/App/Migrations/001.cs", true)]
    [InlineData("src/App/Migrations", "src/Other/Migrations/001.cs", false)]
    [InlineData("docs/?.md", "docs/a.md", true)]
    [InlineData("docs/?.md", "docs/ab.md", false)]
    public void Globs_behave_like_gitignore(string glob, string path, bool excluded) =>
        Assert.Equal(excluded, PathExclusions.Compile([glob], includeDefaults: false).IsExcluded(path));

    [Fact]
    public void Negation_re_includes_and_later_rules_win()
    {
        var exclusions = PathExclusions.Compile(["vendor/", "!vendor/ours/"], includeDefaults: false);
        Assert.True(exclusions.IsExcluded("vendor/lib/x.cs"));
        Assert.False(exclusions.IsExcluded("vendor/ours/x.cs"));
    }

    [Fact]
    public void Directory_check_prunes_matching_folders()
    {
        var exclusions = PathExclusions.Compile(["node_modules_backup/"], includeDefaults: false);
        Assert.True(exclusions.IsDirectoryExcluded("web/node_modules_backup"));
        Assert.False(exclusions.IsDirectoryExcluded("web/src"));
    }

    [Fact]
    public void Ignore_file_parsing_skips_comments_and_blanks()
    {
        var globs = PathExclusions.ParseIgnoreFile("# comment\n\nvendor/\r\n  **/*.min.js  \n");
        Assert.Equal(["vendor/", "**/*.min.js"], globs);
    }

    [Fact]
    public void Policies_match_rules_exactly_by_prefix_or_under_a_path()
    {
        var exact = new SuppressionPolicy(Guid.NewGuid(), WellKnownTenants.DefaultId, null, "privacy.pii.contact", null, "noise", "ana");
        Assert.True(exact.Matches("privacy.pii.contact", "a.cs"));
        Assert.False(exact.Matches("privacy.pii.health", "a.cs"));

        var prefix = new SuppressionPolicy(Guid.NewGuid(), WellKnownTenants.DefaultId, null, "privacy.pii.*", null, "noise", "ana");
        Assert.True(prefix.Matches("privacy.pii.health", null));
        Assert.False(prefix.Matches("privacy.leak.log", null));

        var scoped = new SuppressionPolicy(Guid.NewGuid(), WellKnownTenants.DefaultId, Guid.NewGuid(), "*", "tests/", "test code", "ana");
        Assert.True(scoped.Matches("anything.rule", "src/tests/Fixture.cs"));
        Assert.False(scoped.Matches("anything.rule", "src/App/Service.cs"));
        Assert.False(scoped.Matches("anything.rule", null));

        Assert.Throws<ArgumentException>(() => new SuppressionPolicy(Guid.NewGuid(), WellKnownTenants.DefaultId, null, "*", null, "too broad", "ana"));
        Assert.Throws<ArgumentException>(() => new SuppressionPolicy(Guid.NewGuid(), WellKnownTenants.DefaultId, null, "x", null, "", "ana"));
    }
}
