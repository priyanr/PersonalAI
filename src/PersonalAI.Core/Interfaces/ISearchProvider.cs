namespace PersonalAI.Core.Interfaces;

public record SearchResult(string Title, string Url, string Snippet);

public interface ISearchProvider
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct);
}
