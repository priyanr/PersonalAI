using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agent;

namespace PersonalAI.Core.Interfaces;

public interface IAgentExecutionService
{
    /// <summary>Runs an agent's prompt through a short, unattended agentic loop whose tools are the
    /// agent's allowed Skills (invoked via ISkillExecutionService). Always records an AgentRun — created
    /// as Running before the first provider call, finalized as Succeeded/Failed when the loop ends —
    /// regardless of whether the caller consumes the whole stream. Also updates the parent Agent's
    /// LastRunAt/NextRunAt.</summary>
    IAsyncEnumerable<AgentStreamEvent> RunAsync(Guid agentId, AgentRunTrigger trigger, CancellationToken ct);
}
