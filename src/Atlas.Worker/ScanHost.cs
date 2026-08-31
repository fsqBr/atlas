using System.Diagnostics;
using Atlas.Application.Assessments;
using Atlas.Language.CSharp;
using Atlas.Language.Java;
using Atlas.Language.Python;
using Atlas.Language.VisualBasic;
using Atlas.Scanner.Architecture;
using Atlas.Scanner.Database;
using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Infrastructure;
using Atlas.Scanner.Java;
using Atlas.Scanner.JavaScript;
using Atlas.Scanner.Python;
using Atlas.Scanner.Licenses;
using Atlas.Scanner.Privacy;
using Atlas.Scanner.Quality;
using Atlas.Scanner.Runtime;
using Atlas.Scanner.Secrets;
using Atlas.Scanner.Security;

namespace Atlas.Worker;

public sealed class ScanningOptions
{
    public const string SectionName = "Atlas:Scanning";

    /// <summary>InProcess (default; tests and single-user dev) or ChildProcess (compose: one disposable process per run).</summary>
    public string Isolation { get; set; } = "InProcess";

    public int ChildTimeoutMinutes { get; set; } = 30;

    /// <summary>Managed heap hard limit for the child (DOTNET_GCHeapHardLimit). 0 disables.</summary>
    public int ChildMemoryLimitMb { get; set; } = 2048;

    /// <summary>Wall-clock limit for one scanner inside a run.</summary>
    public int ScannerTimeoutMinutes { get; set; } = 15;

    /// <summary>Workspace file cap after exclusions (0 disables).</summary>
    public int MaxFiles { get; set; } = 250_000;

    public bool UseChildProcess => string.Equals(Isolation, "ChildProcess", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Language analyzers + scanners, registered identically in the worker and in the scan-host child.</summary>
public static class ScanningRegistration
{
    public static IServiceCollection AddAtlasScanning(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScannerRuntime();
        services.AddCSharpLanguage(configuration.GetSection(Tier2Options.SectionName).Get<Tier2Options>());
        services.AddVisualBasicLanguage();
        services.AddJavaLanguage();
        services.AddPythonLanguage();
        services.AddDependencyScanner(configuration["Atlas:Vulnerabilities:OsvBundlePath"]);
        services.AddSecretsScanner(configuration["Atlas:Secrets:HmacKeyBase64"]);
        services.AddSecurityScanner();
        services.AddQualityScanner();
        services.AddPrivacyScanner();
        services.AddDatabaseScanner();
        services.AddInfrastructureScanner();
        services.AddJavaScriptScanner();
        services.AddJavaScanner();
        services.AddPythonScanner();
        services.AddLicenseScanner(configuration.GetSection(LicenseOptions.SectionName).Get<LicenseOptions>());
        services.AddArchitectureScanner();
        return services;
    }
}

/// <summary>
/// The disposable child process: `Atlas.Worker scan-host &lt;request.json&gt; &lt;outcome.json&gt;`.
/// It has no database access and no job lease — it reads a workspace, runs the
/// analyzers and scanners, writes the outcome and exits. Crashes, hangs and
/// memory blow-ups caused by hostile input stay inside it.
/// </summary>
public static class ScanHost
{
    public const string Command = "scan-host";

    public static bool IsScanHostInvocation(string[] args) => args.Length == 3 && args[0] == Command;

    public static async Task<int> RunAsync(string[] args)
    {
        var requestPath = args[1];
        var outcomePath = args[2];

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddAtlasScanning(builder.Configuration);
        var scanning = builder.Configuration.GetSection(ScanningOptions.SectionName).Get<ScanningOptions>() ?? new ScanningOptions();
        builder.Services.AddSingleton(new ScanLimits { ScannerTimeoutMinutes = scanning.ScannerTimeoutMinutes, MaxFiles = scanning.MaxFiles });
        builder.Services.AddSingleton<InProcessScanExecutor>();

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ScanHost");

        try
        {
            var request = await ScanWire.ReadAsync<WorkspaceScanRequest>(requestPath, CancellationToken.None);
            logger.LogInformation("Scan host started for assessment {AssessmentId}: {Scanners} scanner(s) over {Root}.",
                request.AssessmentId, request.ScanIdsByScanner.Count, request.WorkspaceRootPath);

            var outcome = await host.Services.GetRequiredService<InProcessScanExecutor>().ExecuteAsync(request, CancellationToken.None);
            await ScanWire.WriteAsync(outcomePath, outcome, CancellationToken.None);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scan host failed.");
            await Console.Error.WriteLineAsync($"scan-host: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}

/// <summary>
/// IScanExecutor that runs the scan host as a child process with a wall-clock
/// timeout and a managed-heap hard limit; a dead or hung child fails the run's
/// scans, never the worker.
/// </summary>
public sealed class ChildProcessScanExecutor(ScanningOptions options, ILogger<ChildProcessScanExecutor> logger) : IScanExecutor
{
    public async Task<WorkspaceScanOutcome> ExecuteAsync(WorkspaceScanRequest request, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "atlas-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var requestPath = Path.Combine(directory, "request.json");
            var outcomePath = Path.Combine(directory, "outcome.json");
            await ScanWire.WriteAsync(requestPath, request, cancellationToken);

            var startInfo = BuildStartInfo(requestPath, outcomePath, options);
            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the scan host process.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, options.ChildTimeoutMinutes)));
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
                    // Already gone.
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new InvalidOperationException($"Scan host timed out after {options.ChildTimeoutMinutes} minute(s) and was killed.");
            }

            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                var tail = stderr.Length > 1500 ? stderr[^1500..] : stderr;
                throw new InvalidOperationException($"Scan host exited with code {process.ExitCode}. {Summarize(tail)}");
            }

            var outcome = await ScanWire.ReadAsync<WorkspaceScanOutcome>(outcomePath, cancellationToken);
            logger.LogInformation("Scan host finished in {Elapsed:N1}s: {Languages} language(s), {Scanners} scanner result(s).",
                stopwatch.Elapsed.TotalSeconds, outcome.Languages.Count, outcome.Scanners.Count);
            return outcome;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string requestPath, string outcomePath, ScanningOptions options)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the current process path.");
        var assembly = typeof(ScanHost).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            UseShellExecute = false,
        };

        // Launched by `dotnet Atlas.Worker.dll` (container) or by the apphost (dev): reuse whichever started us.
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = processPath;
            startInfo.ArgumentList.Add(assembly);
        }
        else
        {
            startInfo.FileName = processPath;
        }

        startInfo.ArgumentList.Add(ScanHost.Command);
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add(outcomePath);

        if (options.ChildMemoryLimitMb > 0)
        {
            startInfo.Environment["DOTNET_GCHeapHardLimit"] = "0x" + ((long)options.ChildMemoryLimitMb * 1024 * 1024).ToString("X");
        }

        startInfo.Environment["DOTNET_gcServer"] = "0";
        return startInfo;
    }

    private static string Summarize(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("scan-host:", StringComparison.Ordinal) || l.Contains("Exception", StringComparison.Ordinal));
        return line ?? (stderr.Length == 0 ? "No diagnostics." : stderr.Trim());
    }
}
