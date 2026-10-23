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

    /// <summary>Continues a finished run with a user message: the run's transcript is the conversation so far and the
    /// agent keeps its tools, so the user can ask it to change what it built. The reply is appended to the same run.</summary>
    IAsyncEnumerable<AgentStreamEvent> ContinueAsync(Guid agentId, Guid runId, string message, CancellationToken ct);
}
