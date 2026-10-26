using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Sandbox;
using PersonalAI.Core.Services.Settings;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Core.Services.Tools.Gmail;
using PersonalAI.Core.Tools.ComputerControl;
using PersonalAI.Core.Tools.ComputerControl.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools)
            _tools[tool.Name] = tool;
    }

    public IReadOnlyList<AiToolDefinition> Definitions =>
        [.. _tools.Values.Select(t => new AiToolDefinition(t.Name, t.Description, t.ParametersSchemaJson))];

    /// <summary>The tools offered in chat and to skills: everything except tools that only work inside an agent run.</summary>
    public IReadOnlyList<AiToolDefinition> ChatDefinitions =>
        [.. _tools.Values.Where(t => !t.RequiresAgentRun).Select(t => new AiToolDefinition(t.Name, t.Description, t.ParametersSchemaJson))];

    public IReadOnlyList<ITool> All => [.. _tools.Values];

    public bool TryGet(string name, out ITool tool)
    {
        return _tools.TryGetValue(name, out tool!);
    }

    public static ToolRegistry CreateDefault(
        Workspace workspace, ISearchProvider searchProvider, AppSettingsCache appSettings,
        IOptions<NetworkScanSettings> networkScanSettings,
        IHttpClientFactory httpClientFactory, IOptions<HttpToolSettings> httpToolSettings,
        GmailOAuthClient gmailOAuthClient, ToolConfigCache toolConfigCache, IServiceScopeFactory serviceScopeFactory,
        IMouseInputService mouseInputService, IKeyboardInputService keyboardInputService,
        IScreenCaptureService screenCaptureService, IUiaElementScanner uiaElementScanner,
        ISandboxHostClient sandboxHostClient, IOptions<SandboxSettings> sandboxSettings,
        ISandboxHostPowerSignaler sandboxHostPowerSignaler, TimeProvider timeProvider,
        IEnumerable<ITool>? additionalTools = null)
    {
        return new(
    [
        new WebSearchTool(searchProvider, appSettings),
        new ReadFileTool(workspace),
        new WriteFileTool(workspace),
        new EditFileTool(workspace),
        new ListFilesTool(workspace),
        new SearchFilesTool(workspace),
        new NetworkScanTool(networkScanSettings),
        new HttpTool(httpClientFactory, httpToolSettings),
        new GmailTool(gmailOAuthClient, toolConfigCache, serviceScopeFactory, httpClientFactory),
        new ComputerControlTool(mouseInputService, keyboardInputService, screenCaptureService, uiaElementScanner),
        new SandboxTool(sandboxHostClient, sandboxSettings, toolConfigCache, serviceScopeFactory, sandboxHostPowerSignaler, timeProvider),
        .. additionalTools ?? []
    ]);
    }
}
