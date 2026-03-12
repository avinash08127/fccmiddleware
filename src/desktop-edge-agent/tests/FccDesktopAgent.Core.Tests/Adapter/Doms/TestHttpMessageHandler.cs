using System.Net;
using System.Net.Http.Headers;

namespace FccDesktopAgent.Core.Tests.Adapter.Doms;

/// <summary>
/// Minimal fake <see cref="HttpMessageHandler"/> for unit-testing HTTP clients.
/// Captures the last outbound request and returns the configured response.
/// </summary>
internal sealed class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>Request body captured before the content is disposed. Available after the first call.</summary>
    public string? LastRequestBody { get; private set; }

    public TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => _handler = handler;

    /// <summary>Returns a handler that always responds with a 200 JSON body.</summary>
    public static TestHttpMessageHandler RespondJson(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new TestHttpMessageHandler(_ =>
        {
            var content = new StringContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(status) { Content = content };
        });
    }

    /// <summary>Returns a handler that always responds with the given status code and no body.</summary>
    public static TestHttpMessageHandler RespondStatus(HttpStatusCode status)
        => new(_ => new HttpResponseMessage(status));

    /// <summary>Returns a handler that throws <see cref="HttpRequestException"/> to simulate network failure.</summary>
    public static TestHttpMessageHandler ThrowNetworkError()
        => new(_ => throw new HttpRequestException("Simulated network failure"));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        return _handler(request);
    }
}
