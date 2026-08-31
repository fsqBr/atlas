using Atlas.Scanner.Runtime;

namespace Atlas.Scanner.Tests;

public class ContainedArtifactReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-reader-test").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("atlas-reader-outside").FullName;

    public ContainedArtifactReaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "outside");
    }

    [Fact]
    public async Task Reads_contained_file()
    {
        var reader = new ContainedArtifactReader(_root);

        var content = await reader.ReadAllTextAsync(
            Path.Combine("src", "a.cs"), CancellationToken.None);

        Assert.Equal("class A {}", content);
    }

    [Fact]
    public void Enumerates_relative_paths()
    {
        var reader = new ContainedArtifactReader(_root);

        var files = reader.EnumerateFiles("*.cs").ToList();

        Assert.Equal(Path.Combine("src", "a.cs"), Assert.Single(files));
    }

    [Fact]
    public async Task Rejects_parent_traversal()
    {
        var reader = new ContainedArtifactReader(_root);
        var escape = Path.Combine("..", Path.GetFileName(_outside), "secret.txt");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            reader.ReadAllTextAsync(escape, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_absolute_path()
    {
        var reader = new ContainedArtifactReader(_root);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            reader.ReadAllTextAsync(Path.Combine(_outside, "secret.txt"), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_symlink_escaping_root_when_symlinks_available()
    {
        var linkPath = Path.Combine(_root, "sneaky.txt");
        try
        {
            File.CreateSymbolicLink(linkPath, Path.Combine(_outside, "secret.txt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // symlink creation needs privileges on Windows; nothing to assert here
        }

        var reader = new ContainedArtifactReader(_root);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            reader.ReadAllTextAsync("sneaky.txt", CancellationToken.None));
    }

    [Fact]
    public void Missing_root_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            new ContainedArtifactReader(Path.Combine(_root, "missing")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
            Directory.Delete(_outside, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
