using System.Net;
using Atlas.Reporting;

namespace Atlas.Application.Tests;

public class GotenbergPdfRendererTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request);
        }
    }

    [Fact]
    public async Task Posts_index_html_as_multipart_to_the_chromium_route_and_returns_bytes()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("%PDF-1.7 fake"u8.ToArray()),
        }));
        var renderer = new GotenbergPdfRenderer(new HttpClient(handler), new ReportOptions { PdfServiceUrl = "http://atlas-pdf:3000/" });

        Assert.True(renderer.IsAvailable);
        var bytes = await renderer.RenderAsync("<html><body><h1>Relatório</h1></body></html>", CancellationToken.None);

        Assert.Equal("%PDF-1.7 fake", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal("http://atlas-pdf:3000/forms/chromium/convert/html", handler.LastRequest!.RequestUri!.ToString());
        Assert.StartsWith("multipart/form-data", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("filename=index.html", handler.LastBody);
        Assert.Contains("<h1>Relatório</h1>", handler.LastBody);
        Assert.Contains("name=printBackground", handler.LastBody);
        Assert.Contains("name=paperWidth", handler.LastBody);
    }

    [Fact]
    public async Task Service_errors_surface_with_status_and_body()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("chromium busy"),
        }));
        var renderer = new GotenbergPdfRenderer(new HttpClient(handler), new ReportOptions { PdfServiceUrl = "http://atlas-pdf:3000" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.RenderAsync("<p/>", CancellationToken.None));
        Assert.Contains("503", ex.Message);
        Assert.Contains("chromium busy", ex.Message);
    }

    [Fact]
    public async Task Unconfigured_service_is_reported_as_unavailable()
    {
        var renderer = new GotenbergPdfRenderer(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("must not be called"))), new ReportOptions());
        Assert.False(renderer.IsAvailable);
        await Assert.ThrowsAsync<PdfRendererUnavailableException>(() => renderer.RenderAsync("<p/>", CancellationToken.None));
    }
}
