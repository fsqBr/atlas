using System.Diagnostics;
using Atlas.Connector.Git;
using Atlas.Domain.Sources;

namespace Atlas.Connector.Tests;

/// <summary>
/// Connector contract test against a real local bare repository — no network,
/// but the actual git CLI (present on dev machines and CI).
/// </summary>
public class GitCliConnectorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-git-test").FullName;
    private readonly GitCliConnector _connector = new();

    [Fact]
    public async Task Discover_returns_single_repo_named_from_url()
    {
        var repos = await _connector.DiscoverRepositoriesAsync(
            new SourceReference(SourceReference.Kinds.Git, "https://example.test/org/billing.git"),
            CancellationToken.None);

        var repo = Assert.Single(repos);
        Assert.Equal("billing", repo.Name);
    }

    [Fact]
    public async Task Materializes_shallow_clone_with_commit_fingerprint()
    {
        var bareRepo = CreateBareRepoWithOneCommit(out var expectedSha);
        var target = Path.Combine(_root, "clone");

        // file:// transport: plain local paths make git silently ignore --depth.
        var bareRepoUrl = new Uri(bareRepo).AbsoluteUri;

        var materialized = await _connector.MaterializeAsync(
            new SourceReference(SourceReference.Kinds.Git, bareRepoUrl),
            target,
            CancellationToken.None);

        Assert.False(materialized.IsBorrowed);
        Assert.Equal(expectedSha, materialized.CommitSha);
        Assert.True(File.Exists(Path.Combine(target, "readme.txt")));

        var shallowMarker = Path.Combine(target, ".git", "shallow");
        Assert.True(File.Exists(shallowMarker), "clone must be shallow (--depth 1)");
    }

    [Fact]
    public async Task Invalid_remote_throws_instead_of_prompting()
    {
        var target = Path.Combine(_root, "clone-fail");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _connector.MaterializeAsync(
                new SourceReference(SourceReference.Kinds.Git, Path.Combine(_root, "does-not-exist")),
                target,
                CancellationToken.None));

        Assert.Contains("git clone failed", ex.Message);
    }

    private string CreateBareRepoWithOneCommit(out string headSha)
    {
        var bare = Path.Combine(_root, "origin.git");
        var work = Path.Combine(_root, "seed");
        Directory.CreateDirectory(work);

        Git(_root, "init", "--bare", bare);
        Git(_root, "clone", bare, work);
        File.WriteAllText(Path.Combine(work, "readme.txt"), "hello atlas");
        Git(work, "add", ".");
        Git(work, "-c", "user.email=test@atlas.local", "-c", "user.name=Atlas Test",
            "commit", "-m", "initial");
        Git(work, "push", "origin", "HEAD");
        headSha = Git(work, "rev-parse", "HEAD").Trim();

        return bare;
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
        }

        return stdout;
    }

    public void Dispose()
    {
        try
        {
            // Clear read-only attributes git sets on pack files before deleting.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
