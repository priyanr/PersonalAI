using PersonalAI.Core.Services.Agent;

namespace PersonalAI.Core.Interfaces;

public interface ISkillExecutionService
{
    /// <summary>Runs a skill's prompt through a short, unattended agentic loop: its own tool calls,
    /// its own message history (not persisted, not shared with any ChatSession), stopping on a plain-text
    /// reply, the iteration cap, or an unrecoverable tool error. A tool call that would require user
    /// approval (a path outside the workspace sandbox) is rejected instead of pausing, since there is
    /// no user available to approve it mid-run.</summary>
    IAsyncEnumerable<AgentStreamEvent> RunAsync(Guid skillId, string? input, CancellationToken ct);
}
