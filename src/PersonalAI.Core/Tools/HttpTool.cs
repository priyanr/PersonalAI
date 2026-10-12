using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Tools;

/// <summary>Generic HTTP(S) client tool. Every request is checked against an operator-configured
/// allow-list of URL glob patterns (HttpTool:AllowedUrls in appsettings) before anything is sent — a
/// URL that matches no pattern is refused with ToolCompleted(IsError: true). Same philosophy as
/// NetworkScanTool's private-range check: the allow-list IS the safety boundary, so this never goes
/// through ToolApprovalRequired.</summary>
public sealed class HttpTool : ITool
{
    public const string HttpClientName = "http-tool";

    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpToolSettings _settings;
    private readonly IReadOnlyList<Regex> _allowedUrlPatterns;

    public HttpTool(IHttpClientFactory httpClientFactory, IOptions<HttpToolSettings> settings)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _allowedUrlPatterns = [.. _settings.AllowedUrls.Select(GlobToRegex)];
    }

    public string Name => "http_request";

    public string Description => """
        Make an HTTP or HTTPS request to a pre-approved URL and return the response as JSON with
        statusCode, headers, and body. Restricted to an operator-configured allow-list of URL patterns —
        a URL that doesn't match any allowed pattern is refused outright and never sent.

        method: GET, POST, PUT, PATCH, DELETE, HEAD, or OPTIONS (default GET).
        headers: optional object of header name -> value strings, e.g. {"Authorization": "Bearer ...", "Accept": "application/json"}.
        body: optional raw request body string (e.g. a JSON-encoded payload). If a body is given and no
        Content-Type header was set, it defaults to application/json.

        The response body is truncated if it exceeds the configured limit; a "truncated": true field is
        included in the result when that happens.
        """;

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "Full request URL including scheme (http:// or https://). Must match a pre-approved allow-list pattern." },
            "method": { "type": "string", "enum": ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], "description": "HTTP method. Defaults to GET." },
            "headers": {
              "type": "object",
              "additionalProperties": { "type": "string" },
              "description": "Request headers as name/value pairs."
            },
            "body": { "type": "string", "description": "Raw request body, if any." }
          },
          "required": ["url"]
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var urlArg = arguments.GetStringArg("url") ?? throw new ArgumentException("'url' is required.");
        var methodArg = arguments.GetStringArg("method") ?? "GET";

        if (!AllowedMethods.Contains(methodArg))
            return new ToolCompleted(
                $"Unsupported method '{methodArg}'. Must be one of {string.Join(", ", AllowedMethods)}.", IsError: true);

        if (!Uri.TryCreate(urlArg, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new ToolCompleted($"'{urlArg}' is not a valid absolute http(s) URL.", IsError: true);

        if (!IsAllowed(uri))
            return new ToolCompleted(
                $"URL '{urlArg}' is not permitted. It does not match any entry in the configured HttpTool:AllowedUrls allow-list.",
                IsError: true);

        using var request = new HttpRequestMessage(new HttpMethod(methodArg.ToUpperInvariant()), uri);

        string? contentType = null;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in headersEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                if (string.Equals(prop.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = prop.Value.GetString();
                    continue;
                }
                request.Headers.TryAddWithoutValidation(prop.Name, prop.Value.GetString());
            }
        }

        var body = arguments.GetStringArg("body");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolCompleted($"Request to '{urlArg}' timed out after {_settings.TimeoutSeconds}s.", IsError: true);
        }
        catch (HttpRequestException ex)
        {
            return new ToolCompleted($"Request to '{urlArg}' failed: {ex.Message}", IsError: true);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var truncated = false;
            if (responseBody.Length > _settings.MaxResponseBodyChars)
            {
                responseBody = responseBody[.._settings.MaxResponseBodyChars];
                truncated = true;
            }

            var headers = response.Headers
                .Concat(response.Content.Headers)
                .Select(h => new { name = h.Key, value = string.Join(", ", h.Value) })
                .ToList();

            var result = new
            {
                statusCode = (int)response.StatusCode,
                headers,
                body = responseBody,
                truncated
            };

            // A non-2xx HTTP status is not a tool failure — the request round-tripped successfully and
            // the model can see statusCode in the result to decide how to react.
            return new ToolCompleted(JsonSerializer.Serialize(result));
        }
    }

    private bool IsAllowed(Uri uri)
    {
        var url = uri.ToString();
        return _allowedUrlPatterns.Any(p => p.IsMatch(url));
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        foreach (var c in glob)
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString())
            });
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    }
}
