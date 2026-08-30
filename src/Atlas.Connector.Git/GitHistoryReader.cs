using System.Diagnostics;
using System.Globalization;
using Atlas.Domain.Workspaces;

namespace Atlas.Connector.Git;

/// <summary>
/// Reads per-file change history from a git working copy with `git log --numstat`
/// (no code executed, read-only). Used by the git connector after a
/// `--shallow-since` clone and by the local connector when the folder is a
/// repository. Paths are repository-relative with forward slashes.
/// </summary>
public sealed class GitHistoryReader(GitConnectorOptions? options = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public int HistoryMonths => options?.HistoryMonths ?? 0;

    public bool Enabled => HistoryMonths > 0;

    public DateTimeOffset Since => DateTimeOffset.UtcNow.AddMonths(-Math.Max(1, HistoryMonths));

    public async Task<IReadOnlyList<FileChangeFact>> ReadAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        if (!Enabled || !Directory.Exists(Path.Combine(repositoryPath, ".git")))
        {
            return [];
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "log", $"--since={Since:yyyy-MM-dd}", "--numstat", "--no-merges", "--no-renames", "--date=iso-strict",
            "--format=%x01%aE%x09%aI",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var environment = GitCliConnector.AskPassHelper.Create(null);
        environment.Apply(startInfo);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git process.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw;
        }

        var stdout = await stdoutTask;
        await stderrTask;
        if (process.ExitCode != 0)
        {
            return []; // history is an optional enrichment: a repository without history is not an error
        }

        return Parse(stdout);
    }

    /// <summary>Parses `git log --numstat --format=%x01%aE%x09%aI` output.</summary>
    public static IReadOnlyList<FileChangeFact> Parse(string log)
    {
        var commits = new Dictionary<string, int>(StringComparer.Ordinal);
        var added = new Dictionary<string, int>(StringComparer.Ordinal);
        var deleted = new Dictionary<string, int>(StringComparer.Ordinal);
        var authors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var last = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        string? author = null;
        DateTimeOffset? date = null;
        foreach (var rawLine in log.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '\u0001')
            {
                var parts = line[1..].Split('\t');
                author = parts.Length > 0 ? parts[0].Trim().ToLowerInvariant() : "unknown";
                date = parts.Length > 1 && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
                continue;
            }

            var cells = line.Split('\t');
            if (cells.Length < 3)
            {
                continue;
            }

            var path = cells[2].Trim().Replace('\\', '/');
            if (path.Length == 0)
            {
                continue;
            }

            commits[path] = commits.GetValueOrDefault(path) + 1;
            if (int.TryParse(cells[0], out var a))
            {
                added[path] = added.GetValueOrDefault(path) + a;
            }

            if (int.TryParse(cells[1], out var dl))
            {
                deleted[path] = deleted.GetValueOrDefault(path) + dl;
            }

            if (author is not null)
            {
                (authors.TryGetValue(path, out var set) ? set : authors[path] = new HashSet<string>(StringComparer.Ordinal)).Add(author);
            }

            if (date is { } when && (!last.TryGetValue(path, out var existing) || when > existing))
            {
                last[path] = when;
            }
        }

        return commits
            .Select(kv => new FileChangeFact(
                kv.Key, kv.Value, added.GetValueOrDefault(kv.Key), deleted.GetValueOrDefault(kv.Key),
                authors.TryGetValue(kv.Key, out var set) ? set.Count : 0,
                last.TryGetValue(kv.Key, out var when) ? when : null))
            .OrderByDescending(f => f.Commits)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();
    }
}
