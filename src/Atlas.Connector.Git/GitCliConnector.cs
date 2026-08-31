using System.Diagnostics;
using System.Text;
using Atlas.Connector.Abstractions;
using Atlas.Domain.Sources;

namespace Atlas.Connector.Git;

/// <summary>
/// Generic git connector: shallow clone via the git CLI (never
/// blind full clones). The cloned repository is hostile input (009):
/// prompts are disabled, symlinks are not materialized, tags are skipped and
/// the URL is passed as an argument vector, never through a shell.
/// <para>
/// Every git invocation runs against a private, per-clone global config
/// (GIT_CONFIG_GLOBAL): host credential helpers and hooks are out of reach and
/// bind-mounted file:// sources owned by another uid are accepted (they are
/// borrowed, read-only input). Private repositories: the source names a stored
/// credential, resolved through <see cref="ICredentialProvider"/> and handed to
/// git via a temporary GIT_ASKPASS helper reading process-scoped environment
/// variables — the secret never appears in the command line, URL, config or logs.
/// </para>
/// </summary>
public sealed class GitCliConnector(ICredentialProvider? credentials = null, GitConnectorOptions? options = null) : ISourceConnector, IGitCloner
{
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(15);

    public ConnectorDescriptor Descriptor { get; } = new(
        Id: "connector.git",
        Name: "Git (generic)",
        Version: "0.2.0",
        Capabilities: ["materialize", "shallow-clone", "commit-fingerprint", "credentials"]);

    public bool CanHandle(SourceReference source) =>
        source.Kind == SourceReference.Kinds.Git;

    public Task<IReadOnlyList<RepositoryInfo>> DiscoverRepositoriesAsync(
        SourceReference source,
        CancellationToken cancellationToken)
    {
        // A bare git URL identifies exactly one repository; org-wide discovery
        // belongs to provider connectors (GitHub/ADO/GitLab).
        var name = source.Locator.TrimEnd('/').Split('/')[^1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        IReadOnlyList<RepositoryInfo> result =
            [new RepositoryInfo(name, source.Locator, SourceReference.Kinds.Git)];
        return Task.FromResult(result);
    }

    public Task<MaterializedSource> CloneAsync(SourceReference gitSource, string targetDirectory, CancellationToken cancellationToken) =>
        MaterializeAsync(gitSource, targetDirectory, cancellationToken);

    public async Task<MaterializedSource> MaterializeAsync(
        SourceReference source,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        // Egress policy first: nothing is created and git never runs for a host outside the allow-list.
        GitHostPolicy.EnsureAllowed(source.Locator, options ?? new GitConnectorOptions());
        Directory.CreateDirectory(targetDirectory);

        var history = new GitHistoryReader(options);
        var cloneArgs = new List<string> { "clone" };
        if (history.Enabled)
        {
            // Enough history for churn/authorship, still bounded (never a blind full clone, the design notes).
            cloneArgs.Add($"--shallow-since={history.Since:yyyy-MM-dd}");
        }
        else
        {
            cloneArgs.Add("--depth");
            cloneArgs.Add("1");
        }

        cloneArgs.AddRange(["--single-branch", "--no-tags", "-c", "core.symlinks=false"]);

        if (!string.IsNullOrWhiteSpace(source.Branch))
        {
            cloneArgs.Add("--branch");
            cloneArgs.Add(source.Branch);
        }

        cloneArgs.Add("--");
        cloneArgs.Add(source.Locator);
        cloneArgs.Add(targetDirectory);

        using var environment = AskPassHelper.Create(await ResolveCredentialAsync(source, cancellationToken));
        await RunGitAsync(cloneArgs, workingDirectory: null, environment, cancellationToken);

        var commitSha = (await RunGitAsync(
            ["rev-parse", "HEAD"], workingDirectory: targetDirectory, environment, cancellationToken)).Trim();

        var changes = history.Enabled ? await history.ReadAsync(targetDirectory, cancellationToken) : [];
        return new MaterializedSource(targetDirectory, IsBorrowed: false, CommitSha: commitSha, History: changes);
    }

    private async Task<ConnectorCredentialValue?> ResolveCredentialAsync(SourceReference source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.CredentialName))
        {
            return null;
        }

        if (credentials is null)
        {
            throw new InvalidOperationException(
                $"Source requires credential '{source.CredentialName}' but no credential provider is configured.");
        }

        return await credentials.ResolveAsync(source, cancellationToken)
            ?? throw new InvalidOperationException($"Credential '{source.CredentialName}' was not found.");
    }

    private static async Task<string> RunGitAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        AskPassHelper environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        environment.Apply(startInfo);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CloneTimeout);

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
                // Process already exited.
            }

            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments[0]} failed with exit code {process.ExitCode}: {environment.Redact(stderr.Trim())}");
        }

        return stdout;
    }

    /// <summary>
    /// Per-invocation git environment: a private global config (no host helpers,
    /// borrowed read-only sources accepted) plus, when a credential is present, a
    /// temporary GIT_ASKPASS program answering git's username/password prompts
    /// from environment variables scoped to the child process. Deleted on dispose.
    /// </summary>
    internal sealed class AskPassHelper : IDisposable
    {
        internal const string UsernameVariable = "ATLAS_GIT_USERNAME";
        internal const string SecretVariable = "ATLAS_GIT_SECRET";

        /// <summary>Works for GitHub PATs, GitLab tokens and Azure DevOps PATs, which accept any username.</summary>
        internal const string DefaultUsername = "x-access-token";

        private const string GlobalConfig =
            "# Atlas per-clone git configuration (replaces the host's global config for this process only).\n" +
            "[safe]\n\tdirectory = *\n" +
            "[credential]\n\thelper =\n" +
            "[core]\n\tsymlinks = false\n\thooksPath = /dev/null\n";

        private readonly string _directory;
        private readonly ConnectorCredentialValue? _value;

        private AskPassHelper(string directory, string configPath, string? scriptPath, ConnectorCredentialValue? value)
        {
            _directory = directory;
            ConfigPath = configPath;
            ScriptPath = scriptPath;
            _value = value;
        }

        public string ConfigPath { get; }

        /// <summary>Null when no credential is involved.</summary>
        public string? ScriptPath { get; }

        public static AskPassHelper Create(ConnectorCredentialValue? value)
        {
            var directory = Path.Combine(Path.GetTempPath(), "atlas-git-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var configPath = Path.Combine(directory, "gitconfig");
            File.WriteAllText(configPath, GlobalConfig, utf8);

            string? scriptPath = null;
            if (value is not null)
            {
                scriptPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "askpass.bat" : "askpass.sh");
                File.WriteAllText(scriptPath, BuildScript(OperatingSystem.IsWindows()), utf8);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }
            }

            return new AskPassHelper(directory, configPath, scriptPath, value);
        }

        internal static string BuildScript(bool windows) => windows
            ? "@echo off\r\n" +
              "echo %1 | findstr /I /B \"Username\" >nul\r\n" +
              "if %ERRORLEVEL%==0 (echo %" + UsernameVariable + "%) else (echo %" + SecretVariable + "%)\r\n"
            : "#!/bin/sh\n" +
              "case \"$1\" in\n" +
              "  [Uu]sername*) printf '%s\\n' \"$" + UsernameVariable + "\" ;;\n" +
              "  *) printf '%s\\n' \"$" + SecretVariable + "\" ;;\n" +
              "esac\n";

        public void Apply(ProcessStartInfo startInfo)
        {
            // Never allow interactive credential prompts inside a worker.
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = ConfigPath;

            if (_value is null || ScriptPath is null)
            {
                return;
            }

            startInfo.Environment["GIT_ASKPASS"] = ScriptPath;
            startInfo.Environment[UsernameVariable] = string.IsNullOrWhiteSpace(_value.Username) ? DefaultUsername : _value.Username;
            startInfo.Environment[SecretVariable] = _value.Secret;
        }

        public string Redact(string text) =>
            _value is null || string.IsNullOrEmpty(_value.Secret) ? text : text.Replace(_value.Secret, "***", StringComparison.Ordinal);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
