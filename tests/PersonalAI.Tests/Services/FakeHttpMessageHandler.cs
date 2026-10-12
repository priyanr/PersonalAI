using System.Net;
using System.Text;

namespace PersonalAI.Tests.Services;

/// <summary>Returns a fixed NDJSON (or JSON) body for every request, regardless of URL — enough to
/// exercise response-parsing logic without a real HTTP server.</summary>
public sealed class FakeHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    public string? LastRequestBody { get; private set; }
    public Uri? LastRequestUri { get; private set; }
    public string? LastAuthorizationHeader { get; private set; }
    public int RequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastRequestUri = request.RequestUri;
        LastAuthorizationHeader = request.Headers.Authorization?.ToString();
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
        return response;
    }
}
