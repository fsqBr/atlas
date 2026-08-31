namespace Atlas.Reporting;

public sealed class PdfRendererUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Turns the executive report HTML into a PDF. Two implementations: a local
/// Chromium-based browser (developer machines) and the Gotenberg sidecar used in
/// Docker, so the API image itself ships no browser.
/// </summary>
public interface IPdfRenderer
{
    bool IsAvailable { get; }

    /// <summary>Human-readable description of the backend in use (for diagnostics).</summary>
    string Description { get; }

    Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken, string? footerHtml = null);
}
