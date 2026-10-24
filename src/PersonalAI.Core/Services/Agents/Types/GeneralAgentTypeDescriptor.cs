using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Services.Agents.Types;

public sealed class GeneralAgentTypeDescriptor : IAgentTypeDescriptor
{
    public AgentType Type => AgentType.General;
    public string DisplayName => "General";
    public string Description => "Runs its prompt through the agentic loop on a schedule, invoking its skills and tools.";
    public string? SystemPromptPreamble => null;
    public IReadOnlyList<string> RequiredTools => [];

    public (string? NormalizedJson, string? Error) NormalizeConfig(string? typeConfigJson)
    {
        return ("{}", null);
    }
}
