using System.Net.Http.Headers;
using System.Text;

namespace Atlas.Reporting;

/// <summary>
/// PDF rendering through a Gotenberg sidecar (https://gotenberg.dev — Chromium
/// behind an HTTP API). Runs as its own container in Docker Compose; the API
/// only posts Atlas' own HTML and receives bytes back. A4, backgrounds on,
/// no headers/footers, JavaScript not needed.
/// </summary>
public sealed class GotenbergPdfRenderer(HttpClient http, ReportOptions options) : IPdfRenderer
{
    public const string ConvertPath = "/forms/chromium/convert/html";

    private readonly Uri? _baseUrl = string.IsNullOrWhiteSpace(options.PdfServiceUrl) ? null : new Uri(options.PdfServiceUrl.TrimEnd('/') + "/");

    public bool IsAvailable => _baseUrl is not null;

    public string Description => _baseUrl is null ? "gotenberg (not configured)" : $"gotenberg at {_baseUrl}";

    public async Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken, string? footerHtml = null)
    {
        var baseUrl = _baseUrl ?? throw new PdfRendererUnavailableException("PDF service not configured: set Atlas:Report:PdfServiceUrl.");

        using var form = BuildForm(html, footerHtml);
        using var response = await http.PostAsync(new Uri(baseUrl, ConvertPath.TrimStart('/')), form, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"PDF service returned {(int)response.StatusCode}: {(detail.Length > 500 ? detail[..500] : detail)}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    internal static MultipartFormDataContent BuildForm(string html, string? footerHtml = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        form.Add(file, "files", "index.html");
        if (!string.IsNullOrWhiteSpace(footerHtml))
        {
            var footer = new ByteArrayContent(Encoding.UTF8.GetBytes(footerHtml));
            footer.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
            form.Add(footer, "files", "footer.html");
        }

        // A4 in inches; margins handled by the report's own print CSS.
        form.Add(new StringContent("8.27"), "paperWidth");
        form.Add(new StringContent("11.7"), "paperHeight");
        form.Add(new StringContent("0.5"), "marginTop");
        form.Add(new StringContent(string.IsNullOrWhiteSpace(footerHtml) ? "0.5" : "0.7"), "marginBottom");
        form.Add(new StringContent("0.5"), "marginLeft");
        form.Add(new StringContent("0.5"), "marginRight");
        form.Add(new StringContent("true"), "printBackground");
        form.Add(new StringContent("true"), "preferCssPageSize");
        return form;
    }
}
