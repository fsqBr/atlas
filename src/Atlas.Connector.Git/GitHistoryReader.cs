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
            // -M (rename detection): with --no-renames a repository reorganization reset every file's
            // churn to zero, silencing hotspot/silo findings exactly where they matter most.
            "log", $"--since={Since:yyyy-MM-dd}", "--numstat", "--no-merges", "-M", "--date=iso-strict",
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
        var first = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        string? author = null;
        var authorIsBot = false;
        DateTimeOffset? date = null;
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal); // old path -> current path (log is newest-first)
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
                author = parts.Length > 0 ? CanonicalAuthor(parts[0], out authorIsBot) : "unknown";
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

            // Rename entries ("src/{Old => New}/f.cs" or "old.cs => new.cs"): the commit counts on the
            // new path, and older commits on the old path are folded into it.
            if (ResolveRename(path, out var newPath, out var oldPath))
            {
                path = Resolve(aliases, newPath);
                if (oldPath.Length > 0 && !string.Equals(oldPath, path, StringComparison.Ordinal))
                {
                    aliases[oldPath] = path;
                }
            }
            else
            {
                path = Resolve(aliases, path);
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

            if (author is not null && !authorIsBot)
            {
                // Bots (dependabot & friends) do not own knowledge: counting one would hide a
                // genuine single-owner file from the knowledge-silo rule.
                (authors.TryGetValue(path, out var set) ? set : authors[path] = new HashSet<string>(StringComparer.Ordinal)).Add(author);
            }

            if (date is { } when && (!last.TryGetValue(path, out var existing) || when > existing))
            {
                last[path] = when;
            }

            if (date is { } at && (!first.TryGetValue(path, out var earliest) || at < earliest))
            {
                first[path] = at;
            }
        }

        return commits
            .Select(kv => new FileChangeFact(
                kv.Key, kv.Value, added.GetValueOrDefault(kv.Key), deleted.GetValueOrDefault(kv.Key),
                authors.TryGetValue(kv.Key, out var set) ? set.Count : 0,
                last.TryGetValue(kv.Key, out var when) ? when : null,
                first.TryGetValue(kv.Key, out var since) ? since : null))
            .OrderByDescending(f => f.Commits)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>One person, one identity: "12345+ana@users.noreply.github.com" and "ana@corp.com +tag"
    /// aliases collapse where possible, and bot accounts are marked so they never count as an author.</summary>
    private static string CanonicalAuthor(string email, out bool isBot)
    {
        var canonical = email.Trim().ToLowerInvariant();
        var at = canonical.IndexOf('@');
        var local = at > 0 ? canonical[..at] : canonical;
        var domain = at > 0 ? canonical[(at + 1)..] : string.Empty;

        if (domain == "users.noreply.github.com")
        {
            var plus = local.IndexOf('+');
            if (plus >= 0 && local[..plus].All(char.IsAsciiDigit))
            {
                local = local[(plus + 1)..]; // 12345+ana -> ana (the GitHub username)
            }

            isBot = local.EndsWith("[bot]", StringComparison.Ordinal);
            return local;
        }

        var tag = local.IndexOf('+');
        if (tag > 0)
        {
            local = local[..tag]; // ana+ci@corp.com -> ana@corp.com
        }

        isBot = local.EndsWith("[bot]", StringComparison.Ordinal)
            || local is "dependabot" or "renovate" or "github-actions" or "snyk-bot" or "greenkeeper";
        return domain.Length > 0 ? $"{local}@{domain}" : local;
    }

    private static bool ResolveRename(string path, out string newPath, out string oldPath)
    {
        var brace = path.IndexOf('{');
        if (brace >= 0)
        {
            var close = path.IndexOf('}', brace);
            var arrow = path.IndexOf(" => ", brace, StringComparison.Ordinal);
            if (close > arrow && arrow > 0)
            {
                var prefix = path[..brace];
                var suffix = path[(close + 1)..];
                var oldPart = path[(brace + 1)..arrow];
                var newPart = path[(arrow + 4)..close];
                newPath = (prefix + newPart + suffix).Replace("//", "/");
                oldPath = (prefix + oldPart + suffix).Replace("//", "/");
                return true;
            }
        }

        var plainArrow = path.IndexOf(" => ", StringComparison.Ordinal);
        if (plainArrow > 0)
        {
            newPath = path[(plainArrow + 4)..].Trim();
            oldPath = path[..plainArrow].Trim();
            return true;
        }

        newPath = path;
        oldPath = string.Empty;
        return false;
    }

    private static string Resolve(Dictionary<string, string> aliases, string path)
    {
        for (var hops = 0; hops < 16 && aliases.TryGetValue(path, out var target) && !string.Equals(target, path, StringComparison.Ordinal); hops++)
        {
            path = target;
        }

        return path;
    }
}
