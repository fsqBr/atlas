using System.IO.Compression;
using System.Text;
using Atlas.Scanner.Dependencies.Vulnerabilities;

namespace Atlas.Scanner.Tests.Dependencies;

public class OsvBundleSyncTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atlas-osv").FullName;

    private static MemoryStream ZipOf(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Combines_zip_entries_into_one_json_array_and_skips_garbage()
    {
        using var zip = ZipOf(
            ("GHSA-1.json", """{"id":"GHSA-1","affected":[{"package":{"ecosystem":"NuGet","name":"A"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"2.0.0"}]}]}]}"""),
            ("GHSA-2.json", """{"id":"GHSA-2","affected":[]}"""),
            ("README.txt", "not json"),
            ("broken.json", "{ this is not json"),
            ("noid.json", """{"summary":"missing id"}"""));
        using var output = new MemoryStream();

        var count = await OsvBundleSync.WriteBundleAsync(zip, output, CancellationToken.None);

        Assert.Equal(2, count);
        output.Position = 0;
        var source = new OsvJsonBundleVulnerabilitySource(output);
        Assert.StartsWith("osv:2 entries", source.BundleVersion);
        Assert.Single(await source.FindAsync("A", "1.5.0", CancellationToken.None));
    }

    [Fact]
    public async Task Reloading_source_is_empty_until_the_bundle_exists_then_picks_up_changes()
    {
        var path = Path.Combine(_dir, "nuget-osv.json");
        var source = new ReloadingVulnerabilitySource(path);

        Assert.Null(source.BundleVersion);
        Assert.Empty(await source.FindAsync("A", "1.0.0", CancellationToken.None));

        await File.WriteAllTextAsync(path, """[{"id":"V1","affected":[{"package":{"ecosystem":"NuGet","name":"A"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"2.0.0"}]}]}]}]""");
        Assert.Single(await source.FindAsync("A", "1.0.0", CancellationToken.None));
        Assert.StartsWith("osv:1 entries", source.BundleVersion);

        await Task.Delay(20);
        await File.WriteAllTextAsync(path, """[{"id":"V1","affected":[]},{"id":"V2","affected":[{"package":{"ecosystem":"NuGet","name":"B"},"versions":["3.0.0"]}]}]""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Assert.Empty(await source.FindAsync("A", "1.0.0", CancellationToken.None));
        Assert.Single(await source.FindAsync("B", "3.0.0", CancellationToken.None));
        Assert.StartsWith("osv:2 entries", source.BundleVersion);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
