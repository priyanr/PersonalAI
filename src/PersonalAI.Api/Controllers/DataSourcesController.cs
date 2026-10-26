using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Services.Monitoring.Collectors;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

public record ScopeItemDto(Guid Id, string Name, string Kind, IReadOnlyList<string> Connections);
public record DataSourceDto(string Tool, string DisplayName, string ConnectionNoun, string SettingsPath, IReadOnlyList<ScopeItemDto> Items);

/// <summary>The data tools an agent can be scoped to, and what each offers (groups of connections, or single
/// connections). Drives the "Data sources" picker in an agent's settings, so the UI has no per-tool code.</summary>
[ApiController]
[Route("api/data-sources")]
public class DataSourcesController(DataSourceRegistry registry) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DataSourceDto>>> GetAll(CancellationToken ct)
    {
        var result = new List<DataSourceDto>();
        foreach (var source in registry.All.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var items = await source.ListScopeItemsAsync(ct);
            // Where the user adds and edits this tool's connections.
            result.Add(new DataSourceDto(source.ToolName, source.DisplayName, source.ConnectionNoun, "/settings/tools",
                [.. items.Select(i => new ScopeItemDto(i.Id, i.Name, i.Kind, i.ConnectionNames))]));
        }
        return Ok(result);
    }
}
