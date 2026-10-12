using System.Text.Json;
using PersonalAI.Core.Tools;

namespace PersonalAI.Tests.Agent;

public sealed class FakeTool(string name, Func<JsonElement, ToolExecutionContext, ToolExecutionResult> handler) : ITool
{
    public string Name => name;
    public string Description => "fake tool for tests";
    public string ParametersSchemaJson => """{"type":"object","properties":{}}""";

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct) =>
        Task.FromResult(handler(arguments, context));
}
