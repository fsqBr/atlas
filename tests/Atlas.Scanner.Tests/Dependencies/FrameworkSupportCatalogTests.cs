using Atlas.Scanner.Dependencies;

namespace Atlas.Scanner.Tests.Dependencies;

public class FrameworkSupportCatalogTests
{
    private static readonly DateOnly Today = new(2026, 8, 28);

    [Theory]
    [InlineData("v4.5", FrameworkSupportStatus.EndOfLife)]
    [InlineData("v4.6.1", FrameworkSupportStatus.EndOfLife)]
    [InlineData("v4.6.2", FrameworkSupportStatus.EndingSoon)]
    [InlineData("net472", FrameworkSupportStatus.SupportedLegacy)]
    [InlineData("net48", FrameworkSupportStatus.SupportedLegacy)]
    [InlineData("v3.5", FrameworkSupportStatus.SupportedLegacy)]
    [InlineData("netcoreapp3.1", FrameworkSupportStatus.EndOfLife)]
    [InlineData("net6.0", FrameworkSupportStatus.EndOfLife)]
    [InlineData("net8.0", FrameworkSupportStatus.EndingSoon)]
    [InlineData("net9.0", FrameworkSupportStatus.EndOfLife)]
    [InlineData("net10.0", FrameworkSupportStatus.Supported)]
    [InlineData("net8.0-windows", FrameworkSupportStatus.EndingSoon)]
    [InlineData("netstandard2.0", FrameworkSupportStatus.Supported)]
    [InlineData("netstandard1.6", FrameworkSupportStatus.SupportedLegacy)]
    [InlineData("net99.0", FrameworkSupportStatus.Unknown)]
    [InlineData("not-a-tfm", FrameworkSupportStatus.Unknown)]
    public void Classifies_target_frameworks(string moniker, FrameworkSupportStatus expected)
    {
        var result = Assert.Single(FrameworkSupportCatalog.Evaluate("p.csproj", moniker, Today));

        Assert.Equal(expected, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Explanation));
    }

    [Fact]
    public void Multi_targeting_yields_one_verdict_per_framework()
    {
        var results = FrameworkSupportCatalog.Evaluate("p.csproj", "net8.0;net472", Today);

        Assert.Equal(2, results.Count);
        Assert.Equal(FrameworkSupportStatus.EndingSoon, results[0].Status);
        Assert.Equal(FrameworkSupportStatus.SupportedLegacy, results[1].Status);
    }

    [Fact]
    public void Missing_moniker_is_unknown_not_a_crash()
    {
        var result = Assert.Single(FrameworkSupportCatalog.Evaluate("p.csproj", null, Today));

        Assert.Equal(FrameworkSupportStatus.Unknown, result.Status);
    }

    [Fact]
    public void Verdicts_depend_on_the_evaluation_date()
    {
        var beforeEol = Assert.Single(FrameworkSupportCatalog.Evaluate("p.csproj", "net6.0", new DateOnly(2024, 1, 1)));
        var afterEol = Assert.Single(FrameworkSupportCatalog.Evaluate("p.csproj", "net6.0", new DateOnly(2025, 1, 1)));

        Assert.Equal(FrameworkSupportStatus.Supported, beforeEol.Status);
        Assert.Equal(FrameworkSupportStatus.EndOfLife, afterEol.Status);
        Assert.Equal(new DateOnly(2024, 11, 12), afterEol.EndOfLife);
    }
}
