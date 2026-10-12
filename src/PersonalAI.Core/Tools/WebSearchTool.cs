using System.Text;
using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Services.Settings;

namespace PersonalAI.Core.Tools;

public sealed class WebSearchTool(ISearchProvider searchProvider, AppSettingsCache appSettings) : ITool
{
    public string Name => "web_search";

    public string Description =>
        "Search the public web for current information (news, facts, documentation) and return a list of " +
        "results with title, URL, and a snippet. Use this when your own knowledge may be outdated or insufficient. " +
        appSettings.WebSearchGuidance;

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "The search query" }
          },
          "required": ["query"]
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var query = arguments.GetStringArg("query") ?? throw new ArgumentException("'query' is required.");
        var results = await searchProvider.SearchAsync(query, ct);

        if (results.Count == 0)
            return new ToolCompleted("No results found.");

        var sb = new StringBuilder();
        var i = 1;
        foreach (var r in results)
        {
            sb.AppendLine($"{i}. {r.Title}");
            sb.AppendLine($"   {r.Url}");
            sb.AppendLine($"   {r.Snippet}");
            i++;
        }

        return new ToolCompleted(sb.ToString());
    }
}
