extern alias AtlasApi;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ApiProgram = AtlasApi::Program;

namespace Atlas.IntegrationTests;

/// <summary>Folder picker (contained to mounted roots) and browser uploads.</summary>
public sealed class LocalSourcesAndUploadTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-root").FullName;
    private readonly string _uploads = Directory.CreateTempSubdirectory("atlas-up").FullName;

    private WebApplicationFactory<ApiProgram> Factory() => new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:AtlasDb", fixture.ConnectionString);
        builder.UseSetting("Atlas:AutoMigrate", "false");
        builder.UseSetting("Atlas:Vulnerabilities:SyncEnabled", "false");
        builder.UseSetting("Atlas:Secrets:HmacKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Secrets:MasterKeyBase64", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Atlas:Operations:RateLimitPerMinute", "1000");
        builder.UseSetting("Atlas:LocalSources:Roots:0:Path", _root);
        builder.UseSetting("Atlas:LocalSources:Roots:0:Label", "E:/Projetos");
        builder.UseSetting("Atlas:LocalSources:Roots:1:Path", Path.Combine(_root, "does-not-exist"));
        builder.UseSetting("Atlas:LocalSources:Roots:1:Label", "missing");
        builder.UseSetting("Atlas:Uploads:Directory", _uploads);
    });

    private sealed record Root(string Path, string Label, bool Exists);
    private sealed record Folder(string Name, string Path, bool HasDotNetProjects, bool HasSolution, bool IsGitRepo);
    private sealed record Browse(List<Root> Roots, string? Current, string? Parent, List<Folder> Entries);
    private sealed record Uploaded(Guid UploadId, string Name, long Bytes, int Files);

    [Fact]
    public async Task Browse_lists_roots_then_one_level_at_a_time_and_marks_dotnet_folders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Shop", "src"));
        File.WriteAllText(Path.Combine(_root, "Shop", "src", "Shop.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(_root, "Shop", ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "Docs"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        using var factory = Factory();
        using var client = factory.CreateClient();

        var top = await client.GetFromJsonAsync<Browse>("/api/sources/local/browse");
        Assert.Null(top!.Current);
        Assert.Equal(2, top.Roots.Count);
        Assert.True(top.Roots[0].Exists);
        Assert.False(top.Roots[1].Exists);
        Assert.Equal("E:/Projetos", top.Roots[0].Label);

        var level = await client.GetFromJsonAsync<Browse>($"/api/sources/local/browse?path={Uri.EscapeDataString(_root)}");
        Assert.Null(level!.Parent);
        Assert.Equal(["Docs", "Shop"], level.Entries.Select(e => e.Name).ToArray());
        var shop = level.Entries.Single(e => e.Name == "Shop");
        Assert.True(shop.HasDotNetProjects);
        Assert.True(shop.IsGitRepo);
        Assert.False(shop.HasSolution);

        var inside = await client.GetFromJsonAsync<Browse>($"/api/sources/local/browse?path={Uri.EscapeDataString(shop.Path)}");
        Assert.NotNull(inside!.Parent);
        Assert.Equal(["src"], inside.Entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public async Task Browse_refuses_paths_outside_the_roots()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var outside = await client.GetAsync($"/api/sources/local/browse?path={Uri.EscapeDataString(Path.GetTempPath())}");
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);

        var traversal = await client.GetAsync($"/api/sources/local/browse?path={Uri.EscapeDataString(Path.Combine(_root, "..", ".."))}");
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);

        var missing = await client.GetAsync($"/api/sources/local/browse?path={Uri.EscapeDataString(Path.Combine(_root, "nope"))}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Upload_stores_archive_and_manifest_and_rejects_garbage()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("Lettr/Lettr.csproj").Open());
            writer.Write("<Project />");
        }

        using var form = new MultipartFormDataContent
        {
            { new StringContent("LettrLabs.App"), "name" },
            { new StringContent("1"), "files" },
            { new ByteArrayContent(zipStream.ToArray()), "archive", "LettrLabs.App.zip" },
        };
        var response = await client.PostAsync("/api/uploads", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var uploaded = await response.Content.ReadFromJsonAsync<Uploaded>();
        Assert.Equal("LettrLabs.App", uploaded!.Name);
        Assert.True(File.Exists(Path.Combine(_uploads, uploaded.UploadId.ToString("N") + ".zip")));
        Assert.True(File.Exists(Path.Combine(_uploads, uploaded.UploadId.ToString("N") + ".json")));

        using var bad = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "archive", "x.zip" } };
        var rejected = await client.PostAsync("/api/uploads", bad);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Single(Directory.GetFiles(_uploads, "*.zip"));

        var noForm = await client.PostAsJsonAsync("/api/uploads", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noForm.StatusCode);
    }

    [Fact]
    public async Task Replacing_the_upload_swaps_the_archive_deletes_the_old_one_and_queues_a_run()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var first = await UploadAsync(client, "v1");
        var created = await client.PostAsJsonAsync("/api/assessments", new { name = "Lettr", sourceKind = "upload", sourceLocator = first.UploadId.ToString(), branch = (string?)null });
        created.EnsureSuccessStatusCode();
        var assessment = await created.Content.ReadFromJsonAsync<CreatedResponse>();

        var second = await UploadAsync(client, "v2");
        var replaced = await client.PutAsJsonAsync($"/api/assessments/{assessment!.Id}/upload", new { uploadId = second.UploadId.ToString() });
        // The assessment still has its first run pending, so the swap is stored but the new run is refused with 409.
        Assert.Equal(HttpStatusCode.Conflict, replaced.StatusCode);

        var detail = await client.GetFromJsonAsync<DetailResponse>($"/api/assessments/{assessment.Id}");
        Assert.Equal(second.UploadId.ToString(), detail!.SourceLocator);
        Assert.False(File.Exists(Path.Combine(_uploads, first.UploadId.ToString("N") + ".zip")));
        Assert.True(File.Exists(Path.Combine(_uploads, second.UploadId.ToString("N") + ".zip")));

        var unknown = await client.PutAsJsonAsync($"/api/assessments/{assessment.Id}/upload", new { uploadId = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var localCreated = await client.PostAsJsonAsync("/api/assessments", new { name = "Local", sourceKind = "local", sourceLocator = _root, branch = (string?)null });
        var local = await localCreated.Content.ReadFromJsonAsync<CreatedResponse>();
        var wrongKind = await client.PutAsJsonAsync($"/api/assessments/{local!.Id}/upload", new { uploadId = second.UploadId.ToString() });
        Assert.Equal(HttpStatusCode.Conflict, wrongKind.StatusCode);
    }

    [Fact]
    public void Upload_gc_removes_only_old_unreferenced_archives()
    {
        var live = Guid.NewGuid();
        var orphanOld = Guid.NewGuid();
        var orphanFresh = Guid.NewGuid();
        foreach (var id in new[] { live, orphanOld, orphanFresh })
        {
            File.WriteAllBytes(Path.Combine(_uploads, id.ToString("N") + ".zip"), [1]);
            File.WriteAllText(Path.Combine(_uploads, id.ToString("N") + ".json"), "{}");
        }

        var old = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(Path.Combine(_uploads, live.ToString("N") + ".zip"), old);
        File.SetLastWriteTimeUtc(Path.Combine(_uploads, orphanOld.ToString("N") + ".zip"), old);

        var removed = AtlasApi::Atlas.Api.UploadGcService.Sweep(_uploads, [live.ToString()], TimeSpan.FromHours(24), DateTimeOffset.UtcNow);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(Path.Combine(_uploads, live.ToString("N") + ".zip")));
        Assert.True(File.Exists(Path.Combine(_uploads, orphanFresh.ToString("N") + ".zip")));
        Assert.False(File.Exists(Path.Combine(_uploads, orphanOld.ToString("N") + ".zip")));
        Assert.False(File.Exists(Path.Combine(_uploads, orphanOld.ToString("N") + ".json")));
    }

    private sealed record CreatedResponse(Guid Id, Guid JobId);
    private sealed record DetailResponse(Guid Id, string Name, string SourceKind, string SourceLocator);

    private static async Task<Uploaded> UploadAsync(HttpClient client, string marker)
    {
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("App/App.csproj").Open());
            writer.Write($"<Project><!-- {marker} --></Project>");
        }

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Lettr"), "name" },
            { new StringContent("1"), "files" },
            { new ByteArrayContent(zipStream.ToArray()), "archive", "Lettr.zip" },
        };
        var response = await client.PostAsync("/api/uploads", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Uploaded>())!;
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _uploads })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
