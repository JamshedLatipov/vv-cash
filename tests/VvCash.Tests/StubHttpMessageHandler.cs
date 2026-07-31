using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VvCash.Tests;

public class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _responder;
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    /// <summary>Content type of every stubbed response. Defaults to JSON, which is what
    /// every existing caller assumed. UpdateServiceTest overrides it to reproduce the
    /// SPA fallback on proffi.io, which answers a missing path with text/html under
    /// status 200.</summary>
    public string ContentType { get; set; } = "application/json";

    public StubHttpMessageHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder)
        => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        if (request.Content != null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);
        var (code, body) = _responder(request);
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, ContentType)
        };
    }
}
