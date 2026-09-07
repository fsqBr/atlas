using Atlas.Reporting;

namespace Atlas.Application.Tests;

public class ChromiumPdfRendererTests
{
    [Fact]
    public void Configured_path_must_exist()
    {
        Assert.Null(ChromiumPdfRenderer.ResolveExecutable(Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Arguments_are_headless_print_to_pdf_without_header_footer()
    {
        var args = ChromiumPdfRenderer.BuildArguments("/tmp/r/report.html", "/tmp/r/report.pdf", "/tmp/r/profile");

        Assert.Contains("--headless=new", args);
        Assert.Contains("--no-pdf-header-footer", args);
        Assert.Contains("--print-to-pdf=/tmp/r/report.pdf", args);
        Assert.StartsWith("file:///", args[^1]);
        Assert.EndsWith("report.html", args[^1]);
    }

    [Fact]
    public async Task Unavailable_renderer_fails_with_an_actionable_message()
    {
        var renderer = new ChromiumPdfRenderer(new ReportOptions { ChromiumPath = Path.Combine(Path.GetTempPath(), "nope.exe") });
        Assert.False(renderer.IsAvailable);
        var ex = await Assert.ThrowsAsync<PdfRendererUnavailableException>(() => renderer.RenderAsync("<p>x</p>", CancellationToken.None));
        Assert.Contains("Atlas:Report:ChromiumPath", ex.Message);
    }

    [Fact]
    public async Task Renders_a_pdf_when_a_browser_is_installed()
    {
        var renderer = new ChromiumPdfRenderer(new ReportOptions());
        var ci = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" || Environment.GetEnvironmentVariable("CI") == "true";
        if (!renderer.IsAvailable || (ci && Environment.GetEnvironmentVariable("ATLAS_TEST_BROWSER") != "1"))
        {
            // No Chromium here, or a CI runner whose bundled Chrome hangs headless: the local renderer is a developer
            // convenience — production renders through the Gotenberg sidecar. Opt in on CI with ATLAS_TEST_BROWSER=1.
            return;
        }

        var pdf = await renderer.RenderAsync("<!doctype html><html><body><h1>Atlas</h1><p>Relatório — ação</p></body></html>", CancellationToken.None);

        Assert.True(pdf.Length > 1000, $"PDF too small: {pdf.Length} bytes");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }
}
