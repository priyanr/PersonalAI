using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Settings;
using PersonalAI.Core.Services.Skills;
using PersonalAI.Core.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PersonalAI.Core.Services.Sessions;

namespace PersonalAI.Core.Services.Agent;

/// <summary>The chat agent loop: call the model, run the tools it asks for, repeat until it answers. Every entry point
/// reloads the conversation from the database, so pausing for a tool approval is just "save and stop", and a separate
/// request resumes it.</summary>
public sealed class ChatAgentService(
    AppDbContext db,
    IChatSessionRepository repo,
    ISkillsRepository skillsRepo,
    SkillExecutionService skillExecution,
    ChatHistoryCompaction compaction,
    AIProviderRegistry providers,
    ToolRegistry tools,
    IOptions<OllamaSettings> ollamaSettings,
    IOptions<AgentSettings> agentSettings,
    AppSettingsCache appSettings)
{
    /// <summary>Start of the UI-only System message a manual compaction leaves in the transcript.</summary>
    public const string CompactionMarkerPrefix = "Conversation compacted";

    // Scoped, and ChatTurnRunner gives every turn its own scope, so this only holds the current turn's streaming reply.
    private StringBuilder? _inFlightAssistantText;

    /// <summary>Assistant text streamed during the current LLM call but not saved yet: what an interrupted turn would
    /// otherwise lose. Null between calls.</summary>
    public string? InFlightAssistantText => _inFlightAssistantText is { Length: > 0 } sb ? sb.ToString() : null;

    /// <summary>Persists the user message and runs the loop until the model answers, hits the call cap, or a tool
    /// needs the user's approval.</summary>
    public async IAsyncEnumerable<AgentStreamEvent> RunTurnAsync(Guid sessionId, string userText, [EnumeratorCancellation] CancellationToken ct)
    {
        var session = await repo.GetSessionAsync(sessionId, ct) ?? throw new InvalidOperationException($"Session {sessionId} not found.");
        if (string.IsNullOrEmpty(session.Title))
        {
            session.Title = userText.Length > 80 ? userText[..80] + "…" : userText;
            await db.SaveChangesAsync(ct);
        }

        var userMessage = await AppendMessageAsync(new ChatMessage { SessionId = sessionId, Role = MessageRole.User, Content = userText }, ct);
        await SetStatusAsync(sessionId, SessionStatus.Active, ct);
        yield return new TurnStartedEvent(userMessage.SequenceNumber);

        await foreach (var evt in LoopAsync(sessionId, ct))
            yield return evt;
    }

    /// <summary>Approves or denies a pending tool call. Once no approvals are left for that model reply, the turn
    /// continues.</summary>
    public async IAsyncEnumerable<AgentStreamEvent> ResolveApprovalAsync(Guid toolInvocationId, bool approved, [EnumeratorCancellation] CancellationToken ct)
    {
        var invocation = await repo.GetToolInvocationAsync(toolInvocationId, ct)
            ?? throw new InvalidOperationException($"Tool invocation {toolInvocationId} not found.");
        if (invocation.Status != ToolInvocationStatus.PendingApproval)
            throw new InvalidOperationException($"Tool invocation {toolInvocationId} is not pending approval (status: {invocation.Status}).");

        var existing = await repo.GetMessagesAsync(invocation.SessionId, ct);
        yield return new TurnStartedEvent(existing.Count > 0 ? existing[^1].SequenceNumber : 0);

        string content;
        bool isError;
        if (approved)
        {
            var (progress, execution) = ProgressStream.Run(
                onProgress => ExecutePreApprovedAsync(invocation, onProgress, ct),
                msg => new ToolProgressEvent(invocation.Id, invocation.ToolName, msg), ct);
            await foreach (var evt in progress) yield return evt;
            (content, isError) = await execution;
            invocation.Status = ToolInvocationStatus.Approved;
        }
        else
        {
            content = "The user denied this tool execution. Do not retry the same action; ask the user how to proceed instead.";
            isError = true;
            invocation.Status = ToolInvocationStatus.Denied;
        }
        invocation.ResultContent = content;
        invocation.IsError = isError;
        invocation.ResolvedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await AppendToolResultMessageAsync(invocation, ct);
        yield return new ToolResultEvent(invocation.Id, invocation.ToolName, content, isError);

        // The model is called again only once every approval of that reply is decided.
        if ((await repo.GetPendingApprovalsAsync(invocation.RequestingMessageId, ct)).Count > 0)
            yield break;

        await SetStatusAsync(invocation.SessionId, SessionStatus.Active, ct);
        await foreach (var evt in LoopAsync(invocation.SessionId, ct))
            yield return evt;
    }

    /// <summary>Summarizes everything not yet summarized, including the recent messages automatic compaction keeps,
    /// so the next turn sends the summary plus only newer messages.</summary>
    public async IAsyncEnumerable<AgentStreamEvent> CompactHistoryAsync(Guid sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        var session = await repo.GetSessionAsync(sessionId, ct) ?? throw new InvalidOperationException($"Session {sessionId} not found.");
        var history = await repo.GetMessagesAsync(sessionId, ct);
        yield return new TurnStartedEvent(history.Count > 0 ? history[^1].SequenceNumber : 0);

        // As many bounded passes as it takes. Context-excluded messages (an earlier compaction marker) have nothing to
        // summarize, which keeps compacting an already compacted chat a no-op.
        var totalFolded = 0;
        while (true)
        {
            var pending = history.Where(m => m.SequenceNumber > session.SummarizedThroughSequenceNumber && !m.ExcludeFromContext).ToList();
            if (pending.Count == 0) break;

            var (folded, totals) = await compaction.FoldIntoSummaryAsync(session, pending, ct);
            if (totals is { } t) yield return new TokenUsageEvent(t.InputTokens, t.OutputTokens);
            if (folded == 0)
                throw new InvalidOperationException(totalFolded == 0
                    ? "Compaction failed: the model did not return a summary."
                    : $"Compaction stopped partway: {totalFolded} message(s) were summarized, but the model did not return a summary for the rest.");
            totalFolded += folded;
        }

        if (totalFolded > 0)
            await AppendUserNoticeAsync(sessionId, $"{CompactionMarkerPrefix} — {totalFolded} earlier message(s) summarized.", ct);

        yield return new CompactionCompletedEvent(totalFolded);
        yield return new DoneEvent();
    }

    /// <summary>Makes the transcript of a turn that ended abnormally (stopped, failed, server shutdown) consistent again:
    /// unanswered tool calls get an "interrupted" result, the partly streamed reply is saved, and the notice (if any) is
    /// added. Runs on a fresh DI scope: the interrupted one may hold half-saved entities from a cancelled SaveChanges.</summary>
    public async Task FinalizeInterruptedTurnAsync(Guid sessionId, string? partialAssistantText, string interruptionReason, string? systemNotice, CancellationToken ct)
    {
        var history = await repo.GetMessagesAsync(sessionId, ct);
        var lastToolCallMessage = history.LastOrDefault(m => m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ToolCallsJson));
        if (lastToolCallMessage is not null)
        {
            var answered = history
                .Where(m => m.SequenceNumber > lastToolCallMessage.SequenceNumber && m.Role == MessageRole.Tool && m.ToolCallId is not null)
                .Select(m => m.ToolCallId!)
                .ToHashSet(StringComparer.Ordinal);
            // A pending approval is still the user's decision to make, not an orphan.
            foreach (var pending in await repo.GetPendingApprovalsAsync(lastToolCallMessage.Id, ct))
                if (pending.ToolCallId is not null) answered.Add(pending.ToolCallId);

            var calls = JsonSerializer.Deserialize<List<AiToolCall>>(lastToolCallMessage.ToolCallsJson!) ?? [];
            foreach (var call in calls.Where(c => !answered.Contains(c.ToolCallId)))
                await RecordResultAsync(lastToolCallMessage, call, ToolInvocationStatus.Failed,
                    $"Interrupted before this tool call finished ({interruptionReason}). It may or may not have taken " +
                    "effect — check the current state before retrying it.", isError: true, ct);
        }

        if (!string.IsNullOrEmpty(partialAssistantText))
            await AppendMessageAsync(new ChatMessage { SessionId = sessionId, Role = MessageRole.Assistant, Content = partialAssistantText }, ct);
        if (!string.IsNullOrEmpty(systemNotice))
            await AppendMessageAsync(new ChatMessage { SessionId = sessionId, Role = MessageRole.System, Content = systemNotice }, ct);

        await SetStatusAsync(sessionId,
            await repo.HasPendingApprovalsAsync(sessionId, ct) ? SessionStatus.AwaitingApproval : SessionStatus.Active, ct);
    }

    private async IAsyncEnumerable<AgentStreamEvent> LoopAsync(Guid sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        var session = await repo.GetSessionAsync(sessionId, ct) ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        // The session's skills are offered to the model as tools named skill_<name>.
        var enabledSkillIds = new HashSet<Guid>(JsonSerializer.Deserialize<List<Guid>>(session.EnabledSkillIdsJson) ?? []);
        var enabledSkills = enabledSkillIds.Count == 0 ? [] : (await skillsRepo.GetAllAsync(ct)).Where(s => enabledSkillIds.Contains(s.Id)).ToList();
        var skillIdByToolName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var skillToolDefinitions = new List<AiToolDefinition>();
        foreach (var skill in enabledSkills)
        {
            var toolName = SkillToolCatalog.BuildUniqueToolName(skill.Name, skillIdByToolName.Keys);
            skillIdByToolName[toolName] = skill.Id;
            skillToolDefinitions.Add(new AiToolDefinition(toolName,
                string.IsNullOrWhiteSpace(skill.Description) ? $"Runs the '{skill.Name}' skill." : skill.Description,
                SkillToolCatalog.ParametersSchema));
        }

        var disabledToolNames = DisabledToolNames(session);
        var toolDefinitions = session.ToolsEnabled
            ? tools.ChatDefinitions.Where(d => !disabledToolNames.Contains(d.Name)).Concat(skillToolDefinitions).ToList()
            : [];
        var provider = providers.Resolve(session.ProviderKey);
        var think = session.ThinkingEnabled ?? appSettings.ThinkingEnabled;
        var maxIterations = session.MaxLlmCalls ?? agentSettings.Value.MaxIterations;

        var nudged = false;
        // Within one turn, an identical call that already failed is answered with its error instead of run again.
        var failedCalls = new Dictionary<(string Name, string Args), string>();
        // At most one compaction attempt per turn: it is an extra, uncounted provider call, and running it every
        // iteration would multiply a long turn's cost. A large backlog catches up one fold per turn.
        var compactionAttempted = false;
        // The thinking behind this turn's tool-call replies, sent back on the turn's later calls to providers that want
        // it (DeepSeek). Deliberately not saved.
        var turnReasoning = new Dictionary<Guid, string>();

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            // ---- call the model ----
            var history = await repo.GetMessagesAsync(sessionId, ct);
            var compactionFailed = false;
            if (session.TrimHistoryEnabled && !compactionAttempted)
            {
                compactionAttempted = true;
                compactionFailed = !await compaction.CompactIfDueAsync(session, history, ct);
            }
            var wireMessages = BuildWireMessages(session, history, iteration + 1, maxIterations, compactionFailed, turnReasoning);

            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            _inFlightAssistantText = contentBuilder;
            IReadOnlyList<AiToolCall>? toolCalls = null;
            AiTokenUsage? usage = null;
            var hitOutputLimit = false;

            session.TotalLlmCalls++;
            await db.SaveChangesAsync(ct);
            yield return new LlmCallEvent(session.TotalLlmCalls);
            await foreach (var chunk in provider.ChatStreamAsync(session.ModelName, wireMessages, toolDefinitions, session.NumCtx, think, session.ReasoningEffort, ct))
            {
                if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
                {
                    reasoningBuilder.Append(chunk.ReasoningDelta);
                    yield return new ReasoningDeltaEvent(chunk.ReasoningDelta);
                }
                if (!string.IsNullOrEmpty(chunk.ContentDelta))
                {
                    contentBuilder.Append(chunk.ContentDelta);
                    yield return new TextDeltaEvent(chunk.ContentDelta);
                }
                if (chunk.ToolCalls is { Count: > 0 }) toolCalls = chunk.ToolCalls;
                if (chunk.Usage is not null) usage = chunk.Usage;
                if (chunk.HitOutputLimit) hitOutputLimit = true;
            }

            if (usage is not null)
            {
                session.TotalInputTokens += usage.InputTokens;
                session.TotalOutputTokens += usage.OutputTokens;
                await db.SaveChangesAsync(ct);
                yield return new TokenUsageEvent(session.TotalInputTokens, session.TotalOutputTokens);
            }

            // ---- the reply ----
            var assistantContent = contentBuilder.ToString();
            if (string.IsNullOrWhiteSpace(assistantContent) && toolCalls is not { Count: > 0 })
            {
                // Saved as a blank message this would look like a (blank) final answer, so say what happened instead,
                // typically a reasoning model that spent its whole budget thinking. Not retried: it would likely do so again.
                _inFlightAssistantText = null;
                var emptyNotice = ModelReplyDiagnostics.EmptyReplyNotice(
                    session.ProviderKey, hitOutputLimit, reasoningBuilder.Length > 0, usage, provider.SupportsThinkingToggle);
                await AppendUserNoticeAsync(sessionId, emptyNotice, ct);
                yield return new ErrorEvent(emptyNotice);
                yield break;
            }

            var assistantMessage = await AppendMessageAsync(new ChatMessage
            {
                SessionId = sessionId,
                Role = MessageRole.Assistant,
                Content = assistantContent,
                ToolCallsJson = toolCalls is { Count: > 0 } ? JsonSerializer.Serialize(toolCalls) : null
            }, ct);
            _inFlightAssistantText = null;

            if (toolCalls is not { Count: > 0 })
            {
                if (hitOutputLimit)
                {
                    var cutOffNotice = ModelReplyDiagnostics.CutOffAnswerNotice(session.ProviderKey, provider.SupportsThinkingToggle);
                    await AppendUserNoticeAsync(sessionId, cutOffNotice, ct);
                    yield return new ErrorEvent(cutOffNotice);
                    yield break;
                }

                // A reply ending in "Next I'll edit the file:" with no tool call gets one nudge to actually make the call.
                if (!nudged && LooksLikeUnfinishedAnnouncement(assistantContent))
                {
                    nudged = true;
                    await AppendMessageAsync(new ChatMessage
                    {
                        SessionId = sessionId,
                        Role = MessageRole.User,
                        Content = "[system] Your previous message announced an action but contained no tool call. " +
                                  "If you intend to act, emit the tool call now. If the task is complete, say so plainly."
                    }, ct);
                    continue;
                }

                yield return new DoneEvent();
                yield break;
            }

            if (reasoningBuilder.Length > 0)
                turnReasoning[assistantMessage.Id] = reasoningBuilder.ToString();

            // ---- run the tool calls ----
            var pendingCount = 0;
            foreach (var call in toolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var key = (call.Name, call.ArgumentsJson);

                var (refusal, rememberRefusal) = Refusal(call, toolCalls, hitOutputLimit);
                if (refusal is not null)
                {
                    var refused = await RecordResultAsync(assistantMessage, call, ToolInvocationStatus.Failed, refusal, isError: true, ct);
                    if (rememberRefusal) failedCalls[key] = refusal;
                    yield return new ToolResultEvent(refused.Id, call.Name, refusal, true);
                    continue;
                }

                if (skillIdByToolName.TryGetValue(call.Name, out var skillId))
                {
                    var (skillResult, skillFailed) = await skillExecution.RunAsToolAsync(skillId, SkillToolCatalog.ExtractInputArg(call.ArgumentsJson), ct);
                    var skillInvocation = await RecordResultAsync(assistantMessage, call,
                        skillFailed ? ToolInvocationStatus.Failed : ToolInvocationStatus.Completed, skillResult, skillFailed, ct);
                    RememberOutcome(key, skillFailed, skillResult);
                    yield return new ToolResultEvent(skillInvocation.Id, call.Name, skillResult, skillFailed);
                    continue;
                }

                tools.TryGet(call.Name, out var tool); // Refusal already answered an unknown tool
                var context = new ToolExecutionContext(PreApproved: false, Sandbox: new SandboxOwner(SandboxOwnerKind.ChatSession, sessionId));
                var (progress, execution) = ProgressStream.Run(
                    onProgress => ExecuteToolAsync(tool, call.ArgumentsJson, context with { OnProgress = onProgress }, ct),
                    msg => new ToolProgressEvent(Guid.Empty, call.Name, msg), ct);
                await foreach (var evt in progress) yield return evt;
                var (result, executionError) = await execution;

                if (executionError is not null)
                {
                    var failed = await RecordResultAsync(assistantMessage, call, ToolInvocationStatus.Failed, executionError, isError: true, ct);
                    failedCalls[key] = executionError;
                    yield return new ToolResultEvent(failed.Id, call.Name, executionError, true);
                    continue;
                }

                if (result is ToolApprovalRequired approval)
                {
                    var pending = new ToolInvocation
                    {
                        SessionId = sessionId,
                        RequestingMessageId = assistantMessage.Id,
                        ToolCallId = call.ToolCallId,
                        ToolName = call.Name,
                        ArgumentsJson = call.ArgumentsJson,
                        Status = ToolInvocationStatus.PendingApproval,
                        ResolvedPath = approval.ResolvedPath,
                        Reason = approval.Reason
                    };
                    db.ToolInvocations.Add(pending);
                    await db.SaveChangesAsync(ct);
                    pendingCount++;
                    yield return new ApprovalRequiredEvent(pending.Id, call.Name, approval.ResolvedPath, approval.Reason);
                    continue;
                }

                var completed = (ToolCompleted)result!;
                var invocation = await RecordResultAsync(assistantMessage, call, ToolInvocationStatus.Completed, completed.Content, completed.IsError, ct);
                RememberOutcome(key, completed.IsError, completed.Content);
                yield return new ToolResultEvent(invocation.Id, call.Name, completed.Content, completed.IsError);
            }

            if (pendingCount > 0)
            {
                // Resumed by ResolveApprovalAsync once the user has decided every pending call.
                await SetStatusAsync(sessionId, SessionStatus.AwaitingApproval, ct);
                yield break;
            }
        }

        var notice = $"Stopped after {maxIterations} tool iterations without a final answer. Ask the agent to continue if needed.";
        await AppendMessageAsync(new ChatMessage { SessionId = sessionId, Role = MessageRole.System, Content = notice }, ct);
        yield return new ErrorEvent(notice);

        // Why a call is answered with an error instead of being run, and whether to remember that error for repeats.
        (string? Message, bool Remember) Refusal(AiToolCall call, IReadOnlyList<AiToolCall> replyCalls, bool replyHitOutputLimit)
        {
            if (ModelReplyDiagnostics.TruncatedToolCallError(call, replyCalls, replyHitOutputLimit) is { } truncated)
                return (truncated, false);
            if (failedCalls.TryGetValue((call.Name, call.ArgumentsJson), out var previousError))
                return ($"You already made this exact call and it failed with: {previousError} Do not repeat it. Take a different action.", false);
            if (skillIdByToolName.ContainsKey(call.Name))
                return session.ToolsEnabled ? (null, false) : ($"Tool '{call.Name}' is disabled for this session.", true);
            if (!tools.TryGet(call.Name, out _))
                return ($"Unknown tool '{call.Name}'. Available tools: {string.Join(", ", tools.Definitions.Select(d => d.Name).Concat(skillIdByToolName.Keys))}.", true);
            if (!session.ToolsEnabled || disabledToolNames.Contains(call.Name))
                return ($"Tool '{call.Name}' is disabled for this session.", true);
            return (null, false);
        }

        // A failure is remembered so the model can't repeat it; a success may have changed what would fail, so it resets.
        void RememberOutcome((string Name, string Args) key, bool failed, string content)
        {
            if (failed) failedCalls[key] = FirstLine(content);
            else failedCalls.Clear();
        }
    }

    private async Task<(string Content, bool IsError)> ExecutePreApprovedAsync(ToolInvocation invocation, Action<string> onProgress, CancellationToken ct)
    {
        if (!tools.TryGet(invocation.ToolName, out var tool))
            return ($"Unknown tool '{invocation.ToolName}'.", true);

        // The session's tool settings may have changed while the call waited for approval.
        var session = await repo.GetSessionAsync(invocation.SessionId, ct);
        if (session is null || !session.ToolsEnabled || DisabledToolNames(session).Contains(invocation.ToolName))
            return ($"Tool '{invocation.ToolName}' is disabled for this session.", true);

        var context = new ToolExecutionContext(PreApproved: true, Sandbox: new SandboxOwner(SandboxOwnerKind.ChatSession, invocation.SessionId), OnProgress: onProgress);
        var (result, error) = await ExecuteToolAsync(tool, invocation.ArgumentsJson, context, ct);
        return result switch
        {
            null => (error!, true),
            ToolCompleted c => (c.Content, c.IsError),
            ToolApprovalRequired a => ($"Tool still requires approval unexpectedly: {a.Reason}", true),
            _ => ("Unknown tool result.", true)
        };
    }

    /// <summary>Runs one tool call. A bad argument string or a tool that throws becomes an error message for the model.</summary>
    private static async Task<(ToolExecutionResult? Result, string? Error)> ExecuteToolAsync(
        ITool tool, string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        JsonElement arguments;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            arguments = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return (null, $"Tool arguments were not valid JSON: {ex.Message}. Re-emit the tool call with valid JSON arguments, " +
                          "escaping quotes and newlines inside strings. If you were sending a large file, write it in several smaller pieces.");
        }

        try
        {
            return (await tool.ExecuteAsync(arguments, context, ct), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (null, $"Tool execution failed: {ex.Message}");
        }
    }

    /// <summary>The messages sent to the model for one call: system prompt with the call budget, the (compacted) history,
    /// and a placeholder result for any tool call whose result was never saved.</summary>
    private List<AiChatMessage> BuildWireMessages(
        ChatSession session, IReadOnlyList<ChatMessage> history, int callNumber, int maxCalls, bool compactionFailed,
        IReadOnlyDictionary<Guid, string> turnReasoning)
    {
        // The budget is restated on every call so the model can pace itself instead of being cut off mid-task.
        var systemPrompt = (session.SystemPrompt ?? ollamaSettings.Value.DefaultSystemPrompt) +
            $"\n\n[Call budget] This is LLM call {callNumber} of {maxCalls} for this turn. " +
            "Finish and respond with plain text (no further tool calls) before you exceed this budget.";
        var messages = new List<AiChatMessage> { new("system", systemPrompt) };

        foreach (var m in compaction.VisibleHistory(session, history, compactionFailed, messages))
        {
            if (m.ExcludeFromContext) continue;

            var role = m.Role switch
            {
                MessageRole.Assistant => "assistant",
                MessageRole.Tool => "tool",
                MessageRole.System => "system",
                _ => "user"
            };
            var toolCalls = m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ToolCallsJson)
                ? JsonSerializer.Deserialize<List<AiToolCall>>(m.ToolCallsJson)
                : null;
            var content = session.TrimHistoryEnabled && m.Role == MessageRole.Tool
                ? ChatHistoryCompaction.CapContent(m.Content, agentSettings.Value.ToolResultMaxChars)
                : m.Content;
            messages.Add(new AiChatMessage(role, content, toolCalls, m.Role == MessageRole.Tool ? m.ToolCallId : null,
                turnReasoning.GetValueOrDefault(m.Id)));
        }

        return AnswerOrphanedToolCalls(messages);
    }

    /// <summary>Adds a placeholder result for every tool call that has none, e.g. after a turn was interrupted mid-way.
    /// OpenAI-compatible providers reject the whole request (400 "must be followed by tool messages responding to each
    /// tool_call_id") if one is missing. Only the request is patched; the stored history is not.</summary>
    private static List<AiChatMessage> AnswerOrphanedToolCalls(List<AiChatMessage> messages)
    {
        var result = new List<AiChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            result.Add(m);
            if (m.Role != "assistant" || m.ToolCalls is not { Count: > 0 } calls) continue;

            var answered = new HashSet<string>(StringComparer.Ordinal);
            var j = i + 1;
            while (j < messages.Count && messages[j].Role == "tool")
            {
                if (messages[j].ToolCallId is { } id) answered.Add(id);
                result.Add(messages[j]);
                j++;
            }

            foreach (var call in calls.Where(c => !answered.Contains(c.ToolCallId)))
                result.Add(new AiChatMessage("tool",
                    "[No result was recorded for this tool call — the request was interrupted before it completed.]", null, call.ToolCallId));

            i = j - 1; // continue after the tool messages copied above
        }
        return result;
    }

    /// <summary>Tool toggles the session switched off. A tool without a toggle (a test fake, a new tool) stays on: these are
    /// opt-outs on top of ToolsEnabled, not an allowlist.</summary>
    private static HashSet<string> DisabledToolNames(ChatSession session)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!session.WebSearchEnabled) names.Add("web_search");
        if (!session.ReadFileEnabled) names.Add("read_file");
        if (!session.WriteFileEnabled) names.Add("write_file");
        if (!session.EditFileEnabled) names.Add("edit_file");
        if (!session.ListFilesEnabled) names.Add("list_files");
        if (!session.SearchFilesEnabled) names.Add("search_files");
        if (!session.NetworkScanEnabled) names.Add("network_scan");
        if (!session.HttpRequestEnabled) names.Add("http_request");
        if (!session.ComputerControlEnabled) names.Add("computer_control");
        if (!session.SandboxEnabled) names.Add("sandbox");
        if (!session.GmailEnabled) names.Add("gmail");
        return names;
    }

    /// <summary>True when the reply trails off into announcing a next step: it ends with a colon, possibly inside
    /// markdown emphasis ("**Edit response:**").</summary>
    private static bool LooksLikeUnfinishedAnnouncement(string text)
    {
        return text.TrimEnd().TrimEnd('*', '_', '`').TrimEnd().EndsWith(':');
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline >= 0 ? text[..newline].TrimEnd() : text;
    }

    /// <summary>Saves a finished tool call and the Tool message the model sees as its result.</summary>
    private async Task<ToolInvocation> RecordResultAsync(
        ChatMessage requestingMessage, AiToolCall call, ToolInvocationStatus status, string content, bool isError, CancellationToken ct)
    {
        var invocation = new ToolInvocation
        {
            SessionId = requestingMessage.SessionId,
            RequestingMessageId = requestingMessage.Id,
            ToolCallId = call.ToolCallId,
            ToolName = call.Name,
            ArgumentsJson = call.ArgumentsJson,
            Status = status,
            ResultContent = content,
            IsError = isError,
            ResolvedAt = DateTimeOffset.UtcNow
        };
        db.ToolInvocations.Add(invocation);
        await db.SaveChangesAsync(ct);
        await AppendToolResultMessageAsync(invocation, ct);
        return invocation;
    }

    private async Task AppendToolResultMessageAsync(ToolInvocation invocation, CancellationToken ct)
    {
        await AppendMessageAsync(new ChatMessage
        {
            SessionId = invocation.SessionId,
            Role = MessageRole.Tool,
            Content = invocation.ResultContent,
            ToolCallId = invocation.ToolCallId,
            ToolName = invocation.ToolName,
            IsError = invocation.IsError
        }, ct);
    }

    /// <summary>A System message for the user that is never sent to the model (ChatMessage.ExcludeFromContext).</summary>
    private async Task AppendUserNoticeAsync(Guid sessionId, string notice, CancellationToken ct)
    {
        await AppendMessageAsync(new ChatMessage { SessionId = sessionId, Role = MessageRole.System, Content = notice, ExcludeFromContext = true }, ct);
    }

    // SequenceNumber is max+1 within the session, and every new message moves the session's UpdatedAt.
    private async Task<ChatMessage> AppendMessageAsync(ChatMessage message, CancellationToken ct)
    {
        var maxSequence = await db.ChatMessages
            .Where(m => m.SessionId == message.SessionId)
            .Select(m => (int?)m.SequenceNumber)
            .MaxAsync(ct) ?? 0;

        message.SequenceNumber = maxSequence + 1;
        db.ChatMessages.Add(message);

        var session = await db.ChatSessions.FindAsync([message.SessionId], ct);
        if (session is not null)
            session.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return message;
    }

    private async Task SetStatusAsync(Guid sessionId, SessionStatus status, CancellationToken ct)
    {
        var session = await db.ChatSessions.FindAsync([sessionId], ct);
        if (session is null) return;

        session.Status = status;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
