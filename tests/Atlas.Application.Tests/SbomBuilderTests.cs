using System.Text.Json;
using Atlas.Application.Findings;
using Atlas.Domain.Assessments;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;

namespace Atlas.Application.Tests;

public class SbomBuilderTests
{
    private static readonly Assessment Sample = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), WellKnownTenants.DefaultId, "Legacy Shop", new SourceReference("local", "/x"));

    [Fact]
    public void Builds_cyclonedx_with_purls_and_license_expressions()
    {
        const string components = """[{"Ecosystem":"nuget","Id":"Newtonsoft.Json","Version":"13.0.3","License":"MIT","Class":"Permissive"},{"Ecosystem":"npm","Id":"@scope/pkg","Version":"2.0.0","License":null,"Class":"Unknown"}]""";

        var json = SbomBuilder.Build(Sample, components, "0.29.0", new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.NotNull(json);
        using var bom = JsonDocument.Parse(json);
        var root = bom.RootElement;
        Assert.Equal("CycloneDX", root.GetProperty("bomFormat").GetString());
        Assert.Equal("1.5", root.GetProperty("specVersion").GetString());
        Assert.StartsWith("urn:uuid:", root.GetProperty("serialNumber").GetString());
        Assert.Equal("2026-08-29T12:00:00Z", root.GetProperty("metadata").GetProperty("timestamp").GetString());
        Assert.Equal("Legacy Shop", root.GetProperty("metadata").GetProperty("component").GetProperty("name").GetString());
        Assert.Equal("0.29.0", root.GetProperty("metadata").GetProperty("tools")[0].GetProperty("version").GetString());

        var list = root.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("pkg:nuget/Newtonsoft.Json@13.0.3", list[0].GetProperty("purl").GetString());
        Assert.Equal("library", list[0].GetProperty("type").GetString());
        Assert.Equal("MIT", list[0].GetProperty("licenses")[0].GetProperty("expression").GetString());
        Assert.Equal("pkg:npm/@scope/pkg@2.0.0", list[1].GetProperty("purl").GetString());
        Assert.False(list[1].TryGetProperty("licenses", out _)); // unknown license → no licenses element rather than an empty expression
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Ecosystem\":\"nuget\"}")] // an object, not the component array
    public void Returns_null_without_a_usable_component_list(string? components) =>
        Assert.Null(SbomBuilder.Build(Sample, components, "0.29.0", DateTimeOffset.UtcNow));
}
