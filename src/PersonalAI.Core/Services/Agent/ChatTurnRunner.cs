using System.Collections.Concurrent;
using PersonalAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PersonalAI.Core.Services.Agent;

/// <summary>In-memory registry of running chat turns. Singleton, and deliberately not persisted: the API runs
/// as one replica (see CLAUDE.md), and a turn can't survive a process restart anyway — keeping "is running"
/// out of the DB means a crash can never leave a session stuck looking busy. Anything a crash does leave
/// behind (an Assistant tool call with no result) is still papered over at send time by
/// ChatAgentService.RepairOrphanedToolCalls.</summary>
public sealed class ChatTurnRunner(IServiceScopeFactory scopeFactory, ILogger<ChatTurnRunner> logger) : IChatTurnRunner
{
    public const string UserStopReason = "stopped by the user";
    public const string ShutdownStopReason = "the server shut down";
    private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, ChatTurn> _turns = new();

    public ChatTurn? TryStart(Guid sessionId, Func<IChatAgentService, CancellationToken, IAsyncEnumerable<AgentStreamEvent>> body)
    {
        var turn = new ChatTurn(sessionId, DateTimeOffset.UtcNow);
        if (!_turns.TryAdd(sessionId, turn)) return null;
        turn.Completion = Task.Run(() => RunAsync(turn, body));
        return turn;
    }

    public ChatTurn? GetActive(Guid sessionId) => _turns.TryGetValue(sessionId, out var turn) ? turn : null;

    public bool TryStop(Guid sessionId) => GetActive(sessionId)?.RequestStop(UserStopReason) ?? false;

    public async Task StopAllAsync(CancellationToken ct)
    {
        var turns = _turns.Values.ToList();
        foreach (var turn in turns)
            turn.RequestStop(ShutdownStopReason);
        try
        {
            await Task.WhenAll(turns.Select(t => t.Completion)).WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Shutdown timed out waiting for {Count} chat turn(s) to record their interruption", turns.Count);
        }
    }

    private async Task RunAsync(ChatTurn turn, Func<IChatAgentService, CancellationToken, IAsyncEnumerable<AgentStreamEvent>> body)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var agent = scope.ServiceProvider.GetRequiredService<IChatAgentService>();
            try
            {
                await foreach (var evt in body(agent, turn.Token).WithCancellation(turn.Token))
                    turn.Publish(evt);
            }
            catch (OperationCanceledException) when (turn.Token.IsCancellationRequested)
            {
                var reason = turn.StopReason;
                await FinalizeAsync(turn, agent.InFlightAssistantText, reason, $"Turn interrupted: {reason}.");
                turn.Publish(new DoneEvent());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat turn failed for session {SessionId}", turn.SessionId);
                // No System notice here: the error event already tells the live viewer, and the failure itself
                // is usually a provider/config problem the model can't act on anyway.
                await FinalizeAsync(turn, agent.InFlightAssistantText, "the turn failed with an error", systemNotice: null);
                turn.Publish(new ErrorEvent(ModelReplyDiagnostics.ProviderErrorMessage(ex)));
            }
        }
        catch (Exception ex)
        {
            // Scope creation / service resolution failed — nothing ran, so there's nothing to finalize.
            logger.LogError(ex, "Could not start chat turn for session {SessionId}", turn.SessionId);
            turn.Publish(new ErrorEvent(ex.Message));
        }
        finally
        {
            // Unregister BEFORE completing subscribers: a client that refetches the session the moment its
            // stream ends must see "not running", or it would re-attach to a turn that's already over.
            _turns.TryRemove(new KeyValuePair<Guid, ChatTurn>(turn.SessionId, turn));
            turn.Complete();
        }
    }

    private async Task FinalizeAsync(ChatTurn turn, string? partialText, string reason, string? systemNotice)
    {
        try
        {
            // Fresh scope: the interrupted scope's DbContext may still track entities whose SaveChanges was
            // cancelled mid-flight, and saving through it would persist them with stale sequence numbers.
            await using var scope = scopeFactory.CreateAsyncScope();
            var agent = scope.ServiceProvider.GetRequiredService<IChatAgentService>();
            using var timeout = new CancellationTokenSource(FinalizeTimeout);
            await agent.FinalizeInterruptedTurnAsync(turn.SessionId, partialText, reason, systemNotice, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the interruption of the chat turn for session {SessionId}", turn.SessionId);
        }
    }
}
