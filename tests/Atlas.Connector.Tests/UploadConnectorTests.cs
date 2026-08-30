using System.IO.Compression;
using Atlas.Connector.Upload;
using Atlas.Domain.Sources;

namespace Atlas.Connector.Tests;

public class UploadConnectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atlas-uploads").FullName;

    private UploadOptions Options => new() { Directory = _dir, MaxEntries = 1000, MaxExtractedBytes = 10 * 1024 * 1024 };

    private string WriteZip(string id, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_dir, Guid.Parse(id).ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }

    [Fact]
    public async Task Extracts_an_uploaded_folder_into_the_workspace()
    {
        var id = Guid.NewGuid().ToString();
        WriteZip(id, ("Shop/Shop.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"), ("Shop/Program.cs", "class P {}"));
        File.WriteAllText(UploadConnector.ManifestPath(Options, id), """{"Id":"x","Name":"Shop","Bytes":10,"Files":2,"UploadedAtUtc":"2026-08-29T00:00:00Z"}""");
        var connector = new UploadConnector(Options);
        var target = Path.Combine(_dir, "ws");

        var repos = await connector.DiscoverRepositoriesAsync(new SourceReference("upload", id), CancellationToken.None);
        Assert.Equal("Shop", Assert.Single(repos).Name);

        var result = await connector.MaterializeAsync(new SourceReference("upload", id), target, CancellationToken.None);

        Assert.False(result.IsBorrowed);
        Assert.True(File.Exists(Path.Combine(target, "Shop", "Program.cs")));
    }

    [Theory]
    [InlineData("../evil.cs")]
    [InlineData("a/../../evil.cs")]
    [InlineData("/etc/passwd")]
    public async Task Zip_slip_is_refused(string entryName)
    {
        var id = Guid.NewGuid().ToString();
        WriteZip(id, (entryName, "x"));
        var connector = new UploadConnector(Options);

        await Assert.ThrowsAsync<InvalidDataException>(() => connector.MaterializeAsync(new SourceReference("upload", id), Path.Combine(_dir, "ws2"), CancellationToken.None));
    }

    [Fact]
    public async Task Missing_archive_and_bad_ids_are_clear_errors()
    {
        var connector = new UploadConnector(Options);
        await Assert.ThrowsAsync<FileNotFoundException>(() => connector.MaterializeAsync(new SourceReference("upload", Guid.NewGuid().ToString()), Path.Combine(_dir, "ws3"), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => connector.MaterializeAsync(new SourceReference("upload", "../../x"), Path.Combine(_dir, "ws4"), CancellationToken.None));
        Assert.False(connector.CanHandle(new SourceReference("local", "/x")));
        Assert.True(connector.CanHandle(new SourceReference("upload", Guid.NewGuid().ToString())));
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
