using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Services.Agents.Types;

/// <summary>Everything that differs between agent types, so AgentsController and AgentExecutionService
/// stay type-agnostic: typed settings validation, and the prompt framing for the LLM run.</summary>
public interface IAgentTypeDescriptor
{
    AgentType Type { get; }
    string DisplayName { get; }
    string Description { get; }

    /// <summary>Validates raw TypeConfigJson (null/blank means "defaults"). On success returns the
    /// normalized JSON to persist; otherwise an error message.</summary>
    (string? NormalizedJson, string? Error) NormalizeConfig(string? typeConfigJson);

    /// <summary>Text prepended to the agent's own prompt for every LLM run; null when the type adds none.</summary>
    string? SystemPromptPreamble { get; }

    /// <summary>Tools this type always gets, on top of the agent's own allowlist.</summary>
    IReadOnlyList<string> RequiredTools { get; }

    /// <summary>The tools this particular agent gets from its type: the required ones, plus any that depend on its type
    /// settings (a Dashboard &amp; Alerts agent gets one tool per data source it is scoped to).</summary>
    IReadOnlyList<string> ToolsFor(string? typeConfigJson) => RequiredTools;
}
