using PersonalAI.Core.Services.Agent;

namespace PersonalAI.Core.Interfaces;

/// <summary>Runs chat turns in the background, at most one per session, independent of the HTTP request that
/// started them — a page reload or a dropped connection no longer cancels the turn.</summary>
public interface IChatTurnRunner
{
    /// <summary>Starts <paramref name="body"/> on a fresh DI scope's <see cref="IChatAgentService"/>. Returns
    /// null when the session already has a turn running.</summary>
    ChatTurn? TryStart(Guid sessionId, Func<IChatAgentService, CancellationToken, IAsyncEnumerable<AgentStreamEvent>> body);

    /// <summary>The session's running turn, or null.</summary>
    ChatTurn? GetActive(Guid sessionId);

    /// <summary>Asks the session's running turn to stop. False when nothing is running.</summary>
    bool TryStop(Guid sessionId);

    /// <summary>Stops every running turn (server shutdown) and waits for each to record its interruption.</summary>
    Task StopAllAsync(CancellationToken ct);
}
