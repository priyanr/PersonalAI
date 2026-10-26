using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Services.Agents.Types;

public sealed class AgentTypeRegistry(IEnumerable<IAgentTypeDescriptor> descriptors)
{
    private readonly Dictionary<AgentType, IAgentTypeDescriptor> _byType = descriptors.ToDictionary(d => d.Type);

    public IReadOnlyCollection<IAgentTypeDescriptor> All => _byType.Values;

    public IAgentTypeDescriptor Get(AgentType type)
    {
        return _byType.TryGetValue(type, out var d) ? d : throw new InvalidOperationException($"No descriptor registered for agent type {type}.");
    }
}
