using System.Net;
using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Settings;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.Search;

public class SearXngSearchProvider(HttpClient httpClient, IOptions<SearchSettings> settings) : ISearchProvider
{
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        var url = $"{settings.Value.SearxngUrl.TrimEnd('/')}/search" +
                   $"?q={WebUtility.UrlEncode(query)}&format=json&language=en&pageno=1";

        // "categories" and "engines" are additive in SearXNG, not intersected — specifying an engine
        // allowlist while still requesting categories=general,news re-pulls every other engine tagged
        // with those categories, defeating the allowlist. Send categories only when no allowlist is set.
        url += !string.IsNullOrWhiteSpace(settings.Value.Engines)
            ? $"&engines={WebUtility.UrlEncode(settings.Value.Engines)}"
            : "&categories=general,news";

        using var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var output = new List<SearchResult>();
        foreach (var item in results.EnumerateArray())
        {
            if (output.Count >= settings.Value.MaxResults) break;

            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var itemUrl = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var snippet = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            output.Add(new SearchResult(title, itemUrl, snippet));
        }

        return output;
    }
}
