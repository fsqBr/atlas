using System.Diagnostics;
using System.Text;

namespace Atlas.Reporting;

/// <summary>
/// Renders the executive report to PDF with a Chromium-based browser already
/// installed on the machine (Chrome/Edge on a developer box, chromium on a
/// server) via `--headless --print-to-pdf`. Used when no PDF service is
/// configured; Docker Compose uses the Gotenberg sidecar instead. No browser
/// automation library and no network: the HTML is Atlas' own output written to
/// a private temp folder.
/// </summary>
public sealed class ChromiumPdfRenderer(ReportOptions options) : IPdfRenderer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private static readonly string[] CandidateNames =
    [
        "chromium", "chromium-browser", "google-chrome", "google-chrome-stable", "chrome", "msedge",
    ];

    private static readonly string[] WindowsCandidates =
    [
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    ];

    private readonly Lazy<string?> _executable = new(() => ResolveExecutable(options.ChromiumPath));

    /// <summary>Path of the browser that will be used, or null when none is available.</summary>
    public string? Executable => _executable.Value;

    public bool IsAvailable => Executable is not null;

    public string Description => Executable is null ? "local chromium (none found)" : $"local chromium at {Executable}";

    public async Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken, string? footerHtml = null)
    {
        var executable = Executable
            ?? throw new PdfRendererUnavailableException(
                "No Chromium-based browser found. Configure the PDF service (Atlas:Report:PdfServiceUrl, the atlas-pdf container) or install Chrome/Edge/chromium and set Atlas:Report:ChromiumPath.");

        var directory = Path.Combine(Path.GetTempPath(), "atlas-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var htmlPath = Path.Combine(directory, "report.html");
            var pdfPath = Path.Combine(directory, "report.pdf");
            await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in BuildArguments(htmlPath, pdfPath, Path.Combine(directory, "profile")))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the browser process.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);

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

                throw;
            }

            await Task.WhenAll(stderrTask, stdoutTask);

            // On Windows the launcher process can exit before the child that prints has finished
            // writing; wait for the file to exist and stop growing instead of trusting the exit alone.
            var written = process.ExitCode == 0 && await WaitForStableFileAsync(pdfPath, timeoutCts.Token);
            if (!written)
            {
                throw new InvalidOperationException(
                    $"PDF rendering failed (exit code {process.ExitCode}, browser: {executable}): {Truncate(stderrTask.Result.Trim(), 800)}");
            }

            return await File.ReadAllBytesAsync(pdfPath, cancellationToken);
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

    internal static IReadOnlyList<string> BuildArguments(string htmlPath, string pdfPath, string profileDirectory) =>
    [
        "--headless=new",
        "--disable-gpu",
        // The content is Atlas' own HTML (no scripts from assessed code); the container has no user namespaces.
        "--no-sandbox",
        "--disable-dev-shm-usage",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-extensions",
        "--disable-background-networking",
        "--user-data-dir=" + profileDirectory,
        "--run-all-compositor-stages-before-draw",
        "--virtual-time-budget=5000",
        "--no-pdf-header-footer",
        "--print-to-pdf=" + pdfPath,
        new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri,
    ];

    internal static string? ResolveExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe" } : new[] { string.Empty };

        foreach (var name in CandidateNames)
        {
            foreach (var directory in pathDirectories)
            {
                foreach (var extension in extensions)
                {
                    var candidate = Path.Combine(directory, name + extension);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return OperatingSystem.IsWindows() ? WindowsCandidates.FirstOrDefault(File.Exists) : null;
    }

    private static async Task<bool> WaitForStableFileAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        long lastLength = -1;
        var stableRounds = 0;

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 0)
            {
                if (info.Length == lastLength && ++stableRounds >= 2)
                {
                    return true;
                }

                lastLength = info.Length;
            }

            await Task.Delay(150, cancellationToken);
        }

        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
