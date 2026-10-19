using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Settings;
using PersonalAI.Core.Services.Skills;
using PersonalAI.Core.Tools;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.Agent;

/// <summary>The agentic loop, adapted for a stateless HTTP API: every entry point reloads
/// conversation state from the database rather than keeping it in memory, so a tool-approval
/// pause is just "stop and persist", resumable by a completely separate request.</summary>
public sealed class ChatAgentService(
    IChatSessionRepository repo,
    ISkillsRepository skillsRepo,
    ISkillExecutionService skillExecution,
    AIProviderRegistry providers,
    ToolRegistry tools,
    IOptions<OllamaSettings> ollamaSettings,
    IOptions<AgentSettings> agentSettings,
    AppSettingsCache appSettings) : IChatAgentService
{
    // Per-instance on purpose: the service is scoped and ChatTurnRunner gives every turn its own scope,
    // so this only ever holds the current turn's in-progress LLM output.
    private StringBuilder? _inFlightAssistantText;

    public string? InFlightAssistantText => _inFlightAssistantText is { Length: > 0 } sb ? sb.ToString() : null;

    public async Task FinalizeInterruptedTurnAsync(Guid sessionId, string? partialAssistantText, string interruptionReason, string? systemNotice, CancellationToken ct)
    {
        var history = await repo.GetMessagesAsync(sessionId, ct);
        var lastToolCallMessage = history.LastOrDefault(m => m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ToolCallsJson));
        if (lastToolCallMessage is not null)
        {
            var calls = JsonSerializer.Deserialize<List<AiToolCall>>(lastToolCallMessage.ToolCallsJson!) ?? [];
            var answered = history
                .Where(m => m.SequenceNumber > lastToolCallMessage.SequenceNumber && m.Role == MessageRole.Tool && m.ToolCallId is not null)
                .Select(m => m.ToolCallId!)
                .ToHashSet(StringComparer.Ordinal);
            // A pending approval is still a live decision for the user, not an orphan — leave it alone.
            foreach (var pending in await repo.GetPendingApprovalsAsync(lastToolCallMessage.Id, ct))
                if (pending.ToolCallId is not null) answered.Add(pending.ToolCallId);

            foreach (var call in calls.Where(c => !answered.Contains(c.ToolCallId)))
                await RecordFailedCallAsync(sessionId, lastToolCallMessage.Id, call,
                    $"Interrupted before this tool call finished ({interruptionReason}). It may or may not have taken " +
                    "effect — check the current state before retrying it.", ct);
        }

        if (!string.IsNullOrEmpty(partialAssistantText))
            await repo.AppendMessageAsync(new ChatMessage { SessionId = sessionId, Role = MessageRole.Assistant, Content = partialAssistantText }, ct);

        if (!string.IsNullOrEmpty(systemNotice))
            await repo.AppendMessageAsync(new ChatMessage { SessionId = sessionId, Role = MessageRole.System, Content = systemNotice }, ct);

        await repo.SetSessionStatusAsync(sessionId,
            await repo.HasPendingApprovalsAsync(sessionId, ct) ? SessionStatus.AwaitingApproval : SessionStatus.Active, ct);
    }

    public async IAsyncEnumerable<AgentStreamEvent> RunTurnAsync(Guid sessionId, string userText, [EnumeratorCancellation] CancellationToken ct)
    {
        var session = await repo.GetSessionAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        if (string.IsNullOrEmpty(session.Title))
        {
            var title = userText.Length > 80 ? userText[..80] + "…" : userText;
            await repo.SetSessionTitleAsync(sessionId, title, ct);
        }

        var userMessage = await repo.AppendMessageAsync(new ChatMessage { SessionId = sessionId, Role = MessageRole.User, Content = userText }, ct);
        await repo.SetSessionStatusAsync(sessionId, SessionStatus.Active, ct);
        yield return new TurnStartedEvent(userMessage.SequenceNumber);

        await foreach (var evt in LoopAsync(sessionId, ct))
            yield return evt;
    }

    public IAsyncEnumerable<AgentStreamEvent> ContinueTurnAsync(Guid sessionId, CancellationToken ct) => LoopAsync(sessionId, ct);

    public async IAsyncEnumerable<AgentStreamEvent> ResolveApprovalAsync(Guid toolInvocationId, bool approved, [EnumeratorCancellation] CancellationToken ct)
    {
        var invocation = await repo.GetToolInvocationAsync(toolInvocationId, ct)
            ?? throw new InvalidOperationException($"Tool invocation {toolInvocationId} not found.");
        if (invocation.Status != ToolInvocationStatus.PendingApproval)
            throw new InvalidOperationException($"Tool invocation {toolInvocationId} is not pending approval (status: {invocation.Status}).");

        var existing = await repo.GetMessagesAsync(invocation.SessionId, ct);
        yield return new TurnStartedEvent(existing.Count > 0 ? existing[^1].SequenceNumber : 0);

        if (!approved)
        {
            invocation.Status = ToolInvocationStatus.Denied;
            invocation.IsError = true;
            invocation.ResultContent = "The user denied this tool execution. Do not retry the same action; ask the user how to proceed instead.";
            invocation.ResolvedAt = DateTimeOffset.UtcNow;
            await repo.UpdateToolInvocationAsync(invocation, ct);
            await AppendToolResultMessageAsync(invocation, ct);
            yield return new ToolResultEvent(invocation.Id, invocation.ToolName, invocation.ResultContent, true);
        }
        else
        {
            var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentStreamEvent>();
            var executionTask = ExecutePreApprovedAsync(invocation, msg => channel.Writer.TryWrite(new ToolProgressEvent(invocation.Id, invocation.ToolName, msg)), ct);

            while (true)
            {
                var finished = await Task.WhenAny(executionTask, channel.Reader.WaitToReadAsync(ct).AsTask());
                if (finished == executionTask) break;
                // A cancelled WaitToReadAsync completes WhenAny instantly every pass — without this the loop
                // would spin until the tool itself notices cancellation (or forever, if it never does).
                ct.ThrowIfCancellationRequested();
                while (channel.Reader.TryRead(out var evt)) yield return evt;
            }
            channel.Writer.Complete();
            while (channel.Reader.TryRead(out var evt)) yield return evt;

            var (content, isError) = await executionTask;
            invocation.Status = ToolInvocationStatus.Approved;
            invocation.ResultContent = content;
            invocation.IsError = isError;
            invocation.ResolvedAt = DateTimeOffset.UtcNow;
            await repo.UpdateToolInvocationAsync(invocation, ct);
            await AppendToolResultMessageAsync(invocation, ct);
            yield return new ToolResultEvent(invocation.Id, invocation.ToolName, content, isError);
        }

        var stillPending = await repo.GetPendingApprovalsAsync(invocation.RequestingMessageId, ct);
        if (stillPending.Count > 0)
            yield break; // more approvals needed from this same turn before we can call Ollama again

        await repo.SetSessionStatusAsync(invocation.SessionId, SessionStatus.Active, ct);
        await foreach (var evt in LoopAsync(invocation.SessionId, ct))
            yield return evt;
    }

    private async Task<(string Content, bool IsError)> ExecutePreApprovedAsync(ToolInvocation invocation, Action<string>? onProgress, CancellationToken ct)
    {
        if (!tools.TryGet(invocation.ToolName, out var tool))
            return ($"Unknown tool '{invocation.ToolName}'.", true);

        var owningSession = await repo.GetSessionAsync(invocation.SessionId, ct);
        if (owningSession is null || !IsToolPermitted(owningSession, invocation.ToolName))
            return ($"Tool '{invocation.ToolName}' is disabled for this session.", true);

        JsonElement arguments;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(invocation.ArgumentsJson) ? "{}" : invocation.ArgumentsJson);
            arguments = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return ($"Tool arguments were not valid JSON: {ex.Message}.", true);
        }

        try
        {
            var result = await tool.ExecuteAsync(arguments,
                new ToolExecutionContext(PreApproved: true, Sandbox: new SandboxOwner(SandboxOwnerKind.ChatSession, invocation.SessionId), OnProgress: onProgress), ct);
            return result switch
            {
                ToolCompleted c => (c.Content, c.IsError),
                ToolApprovalRequired a => ($"Tool still requires approval unexpectedly: {a.Reason}", true),
                _ => ("Unknown tool result.", true)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ($"Tool execution failed: {ex.Message}", true);
        }
    }

    private async IAsyncEnumerable<AgentStreamEvent> LoopAsync(Guid sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        var session = await repo.GetSessionAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        var enabledSkillIds = new HashSet<Guid>(JsonSerializer.Deserialize<List<Guid>>(session.EnabledSkillIdsJson) ?? []);
        var enabledSkills = enabledSkillIds.Count == 0
            ? []
            : (await skillsRepo.GetAllAsync(ct)).Where(s => enabledSkillIds.Contains(s.Id)).ToList();

        var toolNameToSkillId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var skillToolDefinitions = new List<AiToolDefinition>();
        foreach (var skill in enabledSkills)
        {
            var toolName = SkillToolCatalog.BuildUniqueToolName(skill.Name, toolNameToSkillId.Keys);
            toolNameToSkillId[toolName] = skill.Id;
            skillToolDefinitions.Add(new AiToolDefinition(
                toolName,
                string.IsNullOrWhiteSpace(skill.Description) ? $"Runs the '{skill.Name}' skill." : skill.Description,
                SkillToolCatalog.ParametersSchema));
        }

        var maxIterations = session.MaxLlmCalls ?? agentSettings.Value.MaxIterations;
        var nudged = false;
        // Per-turn: short-circuit an identical call that already failed instead of re-executing it.
        var failedCalls = new Dictionary<(string Name, string Args), string>();
        // At most one compaction (side-LLM-call) attempt per turn, regardless of how many tool-calling
        // iterations the turn makes — an uncounted, uncapped provider call on every iteration would let
        // a long tool-calling turn multiply real provider cost far beyond MaxLlmCalls/TotalLlmCalls.
        // A large backlog still catches up incrementally, just paced one fold per turn as documented in
        // COMPACTION_ARCHITECTURE.md ("catches up over several turns instead of one call") rather than
        // one fold per iteration.
        var compactionAttemptedThisTurn = false;
        // Thinking behind each tool-call message of this turn, keyed by the persisted message's id, so a
        // provider that wants it back (DeepSeek) gets it on the rest of this turn's calls. In memory only:
        // it's meant to be dropped once the turn ends.
        var turnReasoning = new Dictionary<Guid, string>();

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var history = await repo.GetMessagesAsync(sessionId, ct);
            var compactionFailed = false;
            if (session.TrimHistoryEnabled && !compactionAttemptedThisTurn)
            {
                compactionAttemptedThisTurn = true;
                compactionFailed = !await EnsureHistoryCompactedAsync(session, history, ct);
            }
            var wireMessages = BuildWireMessages(session, history, iteration + 1, maxIterations, compactionFailed, turnReasoning);

            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            _inFlightAssistantText = contentBuilder;
            IReadOnlyList<AiToolCall>? toolCalls = null;
            AiTokenUsage? usage = null;
            var hitOutputLimit = false;
            var disabledToolNames = DisabledToolNames(session);
            var toolDefinitions = session.ToolsEnabled
                ? tools.Definitions.Where(d => !disabledToolNames.Contains(d.Name)).Concat(skillToolDefinitions).ToList()
                : [];
            var think = session.ThinkingEnabled ?? appSettings.ThinkingEnabled;
            var provider = providers.Resolve(session.ProviderKey);

            yield return new LlmCallEvent(await repo.IncrementLlmCallsAsync(sessionId, ct));
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

                if (chunk.ToolCalls is { Count: > 0 })
                    toolCalls = chunk.ToolCalls;

                if (chunk.Usage is not null)
                    usage = chunk.Usage;

                if (chunk.HitOutputLimit)
                    hitOutputLimit = true;
            }

            if (usage is not null)
            {
                var (inputTokens, outputTokens) = await repo.AddTokenUsageAsync(sessionId, usage, ct);
                yield return new TokenUsageEvent(inputTokens, outputTokens);
            }

            var assistantContent = contentBuilder.ToString();
            if (string.IsNullOrWhiteSpace(assistantContent) && toolCalls is not { Count: > 0 })
            {
                // Nothing usable came back. Saving it as an empty assistant message would end the turn looking
                // like a (blank) final answer, so say what happened instead — typically a reasoning model that
                // spent its whole output budget thinking. Not retried: a retry would likely burn it again.
                _inFlightAssistantText = null;
                var emptyNotice = ModelReplyDiagnostics.EmptyReplyNotice(
                    session.ProviderKey, hitOutputLimit, reasoningBuilder.Length > 0, usage, provider.SupportsThinkingToggle);
                await AppendUserNoticeAsync(sessionId, emptyNotice, ct);
                yield return new ErrorEvent(emptyNotice);
                yield break;
            }

            var assistantMessage = await repo.AppendMessageAsync(new ChatMessage
            {
                SessionId = sessionId,
                Role = MessageRole.Assistant,
                Content = assistantContent,
                ToolCallsJson = toolCalls is { Count: > 0 } ? JsonSerializer.Serialize(toolCalls) : null
            }, ct);
            _inFlightAssistantText = null;
            if (toolCalls is { Count: > 0 } && reasoningBuilder.Length > 0)
                turnReasoning[assistantMessage.Id] = reasoningBuilder.ToString();

            if (toolCalls is null || toolCalls.Count == 0)
            {
                if (hitOutputLimit)
                {
                    var cutOffNotice = ModelReplyDiagnostics.CutOffAnswerNotice(session.ProviderKey, provider.SupportsThinkingToggle);
                    await AppendUserNoticeAsync(sessionId, cutOffNotice, ct);
                    yield return new ErrorEvent(cutOffNotice);
                    yield break;
                }

                if (!nudged && LooksLikeUnfinishedAnnouncement(assistantContent))
                {
                    nudged = true;
                    await repo.AppendMessageAsync(new ChatMessage
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

            var pendingCount = 0;
            foreach (var call in toolCalls)
            {
                ct.ThrowIfCancellationRequested();

                if (ModelReplyDiagnostics.TruncatedToolCallError(call, toolCalls, hitOutputLimit) is { } truncatedError)
                {
                    var invocation = await RecordFailedCallAsync(sessionId, assistantMessage.Id, call, truncatedError, ct);
                    yield return new ToolResultEvent(invocation.Id, call.Name, truncatedError, true);
                    continue;
                }

                var key = (call.Name, call.ArgumentsJson);
                if (failedCalls.TryGetValue(key, out var previousError))
                {
                    var invocation = await repo.CreateToolInvocationAsync(new ToolInvocation
                    {
                        SessionId = sessionId,
                        RequestingMessageId = assistantMessage.Id,
                        ToolCallId = call.ToolCallId,
                        ToolName = call.Name,
                        ArgumentsJson = call.ArgumentsJson,
                        Status = ToolInvocationStatus.Failed,
                        IsError = true,
                        ResultContent = $"You already made this exact call and it failed with: {previousError} " +
                                        "Do not repeat it. Take a different action.",
                        ResolvedAt = DateTimeOffset.UtcNow
                    }, ct);
                    await AppendToolResultMessageAsync(invocation, ct);
                    yield return new ToolResultEvent(invocation.Id, call.Name, invocation.ResultContent, true);
                    continue;
                }

                if (toolNameToSkillId.TryGetValue(call.Name, out var skillId))
                {
                    if (!session.ToolsEnabled)
                    {
                        var disabledMsg = $"Tool '{call.Name}' is disabled for this session.";
                        var disabledInvocation = await RecordFailedCallAsync(sessionId, assistantMessage.Id, call, disabledMsg, ct);
                        failedCalls[key] = disabledMsg;
                        yield return new ToolResultEvent(disabledInvocation.Id, call.Name, disabledMsg, true);
                        continue;
                    }

                    var skillOutput = new StringBuilder();
                    var skillFailed = false;
                    try
                    {
                        await foreach (var evt in skillExecution.RunAsync(skillId, SkillToolCatalog.ExtractInputArg(call.ArgumentsJson), ct))
                        {
                            switch (evt)
                            {
                                case TextDeltaEvent d:
                                    skillOutput.Append(d.Text);
                                    break;
                                case ToolResultEvent t when t.IsError:
                                    skillFailed = true;
                                    break;
                                case ErrorEvent e:
                                    skillFailed = true;
                                    if (skillOutput.Length > 0) skillOutput.Append('\n');
                                    skillOutput.Append(e.Message);
                                    break;
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        skillFailed = true;
                        skillOutput.Append(ex.Message);
                    }

                    var skillResult = skillOutput.Length > 0
                        ? skillOutput.ToString()
                        : skillFailed ? "Skill run failed." : "Skill produced no output.";
                    var skillInvocation = await repo.CreateToolInvocationAsync(new ToolInvocation
                    {
                        SessionId = sessionId,
                        RequestingMessageId = assistantMessage.Id,
                        ToolCallId = call.ToolCallId,
                        ToolName = call.Name,
                        ArgumentsJson = call.ArgumentsJson,
                        Status = skillFailed ? ToolInvocationStatus.Failed : ToolInvocationStatus.Completed,
                        ResultContent = skillResult,
                        IsError = skillFailed,
                        ResolvedAt = DateTimeOffset.UtcNow
                    }, ct);
                    await AppendToolResultMessageAsync(skillInvocation, ct);
                    if (skillFailed) failedCalls[key] = FirstLine(skillResult);
                    else failedCalls.Clear(); // state changed — a previously failed call may now succeed
                    yield return new ToolResultEvent(skillInvocation.Id, call.Name, skillResult, skillFailed);
                    continue;
                }

                if (!tools.TryGet(call.Name, out var tool))
                {
                    var errMsg = $"Unknown tool '{call.Name}'. Available tools: {string.Join(", ", tools.Definitions.Select(d => d.Name).Concat(toolNameToSkillId.Keys))}.";
                    var invocation = await RecordFailedCallAsync(sessionId, assistantMessage.Id, call, errMsg, ct);
                    failedCalls[key] = errMsg;
                    yield return new ToolResultEvent(invocation.Id, call.Name, errMsg, true);
                    continue;
                }

                if (!session.ToolsEnabled || disabledToolNames.Contains(call.Name))
                {
                    var errMsg = $"Tool '{call.Name}' is disabled for this session.";
                    var invocation = await RecordFailedCallAsync(sessionId, assistantMessage.Id, call, errMsg, ct);
                    failedCalls[key] = errMsg;
                    yield return new ToolResultEvent(invocation.Id, call.Name, errMsg, true);
                    continue;
                }

                JsonElement arguments = default;
                string? argParseError = null;
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                    arguments = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    argParseError = $"Tool arguments were not valid JSON: {ex.Message}. Re-emit the tool call with valid JSON arguments, " +
                                    "escaping quotes and newlines inside strings. If you were sending a large file, write it in several smaller pieces.";
                }

                if (argParseError is not null)
                {
                    var invocation = await RecordFailedCallAsync(sessionId, assistantMessage.Id, call, argParseError, ct);
                    failedCalls[key] = argParseError;
                    yield return new ToolResultEvent(invocation.Id, call.Name, argParseError, true);
                    continue;
                }

                ToolExecutionResult? result = null;
                string? executionError = null;

                var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentStreamEvent>();
                // The task runs the execution and catches errors internally, so the outer yield loop
                // doesn't sit inside a try-catch (which C# forbids for async enumerables).
                var executionTask = Task.Run(async () =>
                {
                    try
                    {
                        var res = await tool.ExecuteAsync(arguments,
                            new ToolExecutionContext(PreApproved: false, Sandbox: new SandboxOwner(SandboxOwnerKind.ChatSession, sessionId), OnProgress: msg => channel.Writer.TryWrite(new ToolProgressEvent(Guid.Empty, call.Name, msg))), ct);
                        return (Result: res, Error: (string?)null);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return (Result: (ToolExecutionResult?)null, Error: $"Tool execution failed: {ex.Message}");
                    }
                    finally
                    {
                        channel.Writer.Complete();
                    }
                }, ct);

                while (true)
                {
                    var finished = await Task.WhenAny(executionTask, channel.Reader.WaitToReadAsync(ct).AsTask());
                    if (finished == executionTask) break;
                    ct.ThrowIfCancellationRequested(); // see ResolveApprovalAsync — avoids spinning on a cancelled wait
                    while (channel.Reader.TryRead(out var evt)) yield return evt;
                }
                while (channel.Reader.TryRead(out var evt)) yield return evt;

                var taskResult = await executionTask;
                result = taskResult.Result;
                executionError = taskResult.Error;

                if (executionError is not null)
                {
                    var invocation = await RecordFailedCallAsync(sessionId, assistantMessage.Id, call, executionError, ct);
                    failedCalls[key] = executionError;
                    yield return new ToolResultEvent(invocation.Id, call.Name, executionError, true);
                    continue;
                }

                switch (result!)
                {
                    case ToolApprovalRequired approvalRequired:
                        var pendingInvocation = await repo.CreateToolInvocationAsync(new ToolInvocation
                        {
                            SessionId = sessionId,
                            RequestingMessageId = assistantMessage.Id,
                            ToolCallId = call.ToolCallId,
                            ToolName = call.Name,
                            ArgumentsJson = call.ArgumentsJson,
                            Status = ToolInvocationStatus.PendingApproval,
                            ResolvedPath = approvalRequired.ResolvedPath,
                            Reason = approvalRequired.Reason
                        }, ct);
                        pendingCount++;
                        yield return new ApprovalRequiredEvent(pendingInvocation.Id, call.Name, approvalRequired.ResolvedPath, approvalRequired.Reason);
                        break;

                    case ToolCompleted completed:
                        var completedInvocation = await repo.CreateToolInvocationAsync(new ToolInvocation
                        {
                            SessionId = sessionId,
                            RequestingMessageId = assistantMessage.Id,
                            ToolCallId = call.ToolCallId,
                            ToolName = call.Name,
                            ArgumentsJson = call.ArgumentsJson,
                            Status = ToolInvocationStatus.Completed,
                            ResultContent = completed.Content,
                            IsError = completed.IsError,
                            ResolvedAt = DateTimeOffset.UtcNow
                        }, ct);
                        await AppendToolResultMessageAsync(completedInvocation, ct);
                        if (completed.IsError)
                            failedCalls[key] = FirstLine(completed.Content);
                        else
                            failedCalls.Clear(); // state changed — a previously failed call may now succeed
                        yield return new ToolResultEvent(completedInvocation.Id, call.Name, completed.Content, completed.IsError);
                        break;
                }
            }

            if (pendingCount > 0)
            {
                await repo.SetSessionStatusAsync(sessionId, SessionStatus.AwaitingApproval, ct);
                yield break; // resumed later via ResolveApprovalAsync once all pending calls are decided
            }
        }

        var notice = $"Stopped after {maxIterations} tool iterations without a final answer. Ask the agent to continue if needed.";
        await repo.AppendMessageAsync(new ChatMessage { SessionId = sessionId, Role = MessageRole.System, Content = notice }, ct);
        yield return new ErrorEvent(notice);
    }

    /// <summary>A System-role notice for the user that is never sent back to the model (see ChatMessage.ExcludeFromContext).</summary>
    private Task<ChatMessage> AppendUserNoticeAsync(Guid sessionId, string notice, CancellationToken ct) =>
        repo.AppendMessageAsync(new ChatMessage
        {
            SessionId = sessionId,
            Role = MessageRole.System,
            Content = notice,
            ExcludeFromContext = true
        }, ct);

    private async Task<ToolInvocation> RecordFailedCallAsync(Guid sessionId, Guid requestingMessageId, AiToolCall call, string errorMessage, CancellationToken ct)
    {
        var invocation = await repo.CreateToolInvocationAsync(new ToolInvocation
        {
            SessionId = sessionId,
            RequestingMessageId = requestingMessageId,
            ToolCallId = call.ToolCallId,
            ToolName = call.Name,
            ArgumentsJson = call.ArgumentsJson,
            Status = ToolInvocationStatus.Failed,
            ResultContent = errorMessage,
            IsError = true,
            ResolvedAt = DateTimeOffset.UtcNow
        }, ct);
        await AppendToolResultMessageAsync(invocation, ct);
        return invocation;
    }

    /// <summary>Names explicitly turned off among the known per-tool toggles. Any other registered
    /// tool (e.g. a test fake, or a future tool with no dedicated toggle yet) is left untouched —
    /// these are opt-out flags on top of ToolsEnabled, not an allowlist.</summary>
    private static IReadOnlySet<string> DisabledToolNames(ChatSession session)
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

    private static bool IsToolPermitted(ChatSession session, string toolName) =>
        session.ToolsEnabled && !DisabledToolNames(session).Contains(toolName);

    private async Task AppendToolResultMessageAsync(ToolInvocation invocation, CancellationToken ct) =>
        await repo.AppendMessageAsync(new ChatMessage
        {
            SessionId = invocation.SessionId,
            Role = MessageRole.Tool,
            Content = invocation.ResultContent,
            ToolCallId = invocation.ToolCallId,
            ToolName = invocation.ToolName,
            IsError = invocation.IsError
        }, ct);

    private List<AiChatMessage> BuildWireMessages(
        ChatSession session, IReadOnlyList<ChatMessage> history, int callNumber, int maxCalls, bool forceLegacyDrop,
        IReadOnlyDictionary<Guid, string>? turnReasoning = null)
    {
        var systemPrompt = session.SystemPrompt ?? ollamaSettings.Value.DefaultSystemPrompt;
        // Relayed fresh every call so the model can see its own budget and pace itself instead of being
        // cut off mid-task when the cap is hit.
        systemPrompt += $"\n\n[Call budget] This is LLM call {callNumber} of {maxCalls} for this turn. " +
            "Finish and respond with plain text (no further tool calls) before you exceed this budget.";
        var messages = new List<AiChatMessage> { new("system", systemPrompt) };

        IReadOnlyList<ChatMessage> effectiveHistory;
        if (!session.TrimHistoryEnabled)
            // No automatic compaction, but a summary from a manual compaction still replaces what it covers.
            effectiveHistory = ApplyCompaction(session, history, messages);
        else if (forceLegacyDrop)
            // EnsureHistoryCompactedAsync's summarization call failed this turn — fall back to the
            // old drop-with-note behavior rather than sending a stale/partial summary.
            effectiveHistory = DropOldHistory(history, messages);
        else
            effectiveHistory = ApplyCompaction(session, history, messages);

        foreach (var m in effectiveHistory)
        {
            if (m.ExcludeFromContext) continue;

            var role = m.Role switch
            {
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                MessageRole.Tool => "tool",
                MessageRole.System => "system",
                _ => "user"
            };

            List<AiToolCall>? toolCalls = null;
            if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ToolCallsJson))
                toolCalls = JsonSerializer.Deserialize<List<AiToolCall>>(m.ToolCallsJson);

            var content = session.TrimHistoryEnabled && m.Role == MessageRole.Tool
                ? CapContent(m.Content, agentSettings.Value.ToolResultMaxChars)
                : m.Content;

            var reasoning = turnReasoning is not null && turnReasoning.TryGetValue(m.Id, out var r) ? r : null;
            messages.Add(new AiChatMessage(role, content, toolCalls, m.Role == MessageRole.Tool ? m.ToolCallId : null, reasoning));
        }

        return RepairOrphanedToolCalls(messages);
    }

    /// <summary>Synthesizes a placeholder "tool" message for any tool_call_id an Assistant message's
    /// ToolCalls promised a response for but history has none of — e.g. the request that would have
    /// appended it was cancelled mid-turn (client disconnect, server restart) partway through the
    /// tool-call loop in <see cref="LoopAsync"/>, leaving the Assistant message persisted with its full
    /// ToolCallsJson but only some (or none) of the matching Tool-role result messages ever written.
    /// Every OpenAI-compatible provider (including Azure's non-OpenAI model deployments) rejects the
    /// whole request outright — 400 "must be followed by tool messages responding to each tool_call_id"
    /// — if even one is missing, so this repairs it defensively at send time rather than requiring the
    /// gap to be fixed at its source, which could reappear from any future interruption. Purely
    /// additive to the wire payload — nothing is persisted back to the session's stored history.</summary>
    private static List<AiChatMessage> RepairOrphanedToolCalls(List<AiChatMessage> messages)
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

            foreach (var call in calls)
            {
                if (!answered.Contains(call.ToolCallId))
                    result.Add(new AiChatMessage("tool",
                        "[No result was recorded for this tool call — the request was interrupted before it completed.]",
                        null, call.ToolCallId));
            }

            i = j - 1; // resume just past the contiguous tool-message run we already copied above
        }
        return result;
    }

    /// <summary>Replaces every message already folded into session.HistorySummary (SequenceNumber &lt;=
    /// SummarizedThroughSequenceNumber) with one system message carrying the summary, appended to
    /// <paramref name="messages"/>; everything newer is sent raw. Nothing has been compacted yet
    /// (SummarizedThroughSequenceNumber == 0) simply sends everything raw, same as before compaction
    /// ever ran. See COMPACTION_ARCHITECTURE.md.</summary>
    private static IReadOnlyList<ChatMessage> ApplyCompaction(ChatSession session, IReadOnlyList<ChatMessage> history, List<AiChatMessage> messages)
    {
        if (session.SummarizedThroughSequenceNumber <= 0 || string.IsNullOrEmpty(session.HistorySummary))
            return history;

        messages.Add(new AiChatMessage("system", $"[Summary of earlier conversation]\n{session.HistorySummary}"));
        return history.Where(m => m.SequenceNumber > session.SummarizedThroughSequenceNumber).ToList();
    }

    /// <summary>Legacy fallback: keeps only the most recent HistoryKeepRecentMessages messages,
    /// appending a system note about the rest to <paramref name="messages"/> when anything was dropped.
    /// Used only when a compaction pass's summarization call failed this turn (see BuildWireMessages).</summary>
    private IReadOnlyList<ChatMessage> DropOldHistory(IReadOnlyList<ChatMessage> history, List<AiChatMessage> messages)
    {
        var start = KeepWindowStartIndex(history, agentSettings.Value.HistoryKeepRecentMessages);
        if (start == 0) return history;

        messages.Add(new AiChatMessage("system", $"[{start} earlier message(s) omitted to fit the model's context window.]"));
        return history.Skip(start).ToList();
    }

    /// <summary>Index into <paramref name="history"/> where the most-recent-<paramref name="keep"/>
    /// window begins. Never starts the window on a Tool message — walks forward past any leading Tool
    /// messages — so a tool result is never sent without the assistant call that requested it.</summary>
    private static int KeepWindowStartIndex(IReadOnlyList<ChatMessage> history, int keep)
    {
        if (history.Count <= keep) return 0;

        var start = history.Count - keep;
        while (start < history.Count && history[start].Role == MessageRole.Tool)
            start++;
        return start;
    }

    /// <summary>Folds history older than the keep window that hasn't been summarized yet into
    /// session.HistorySummary, once either dual-trigger threshold (CompactionBatchSize messages or
    /// CompactionMaxCharsPerCall chars pending) is crossed. Persists the result on success. Returns
    /// false — leaving session.HistorySummary/SummarizedThroughSequenceNumber untouched — if the
    /// summarization call failed, so the caller falls back to DropOldHistory for this turn only.
    /// See COMPACTION_ARCHITECTURE.md.</summary>
    private async Task<bool> EnsureHistoryCompactedAsync(ChatSession session, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        var keepWindowStart = KeepWindowStartIndex(history, agentSettings.Value.HistoryKeepRecentMessages);
        if (keepWindowStart == 0) return true; // nothing older than the keep window yet

        var pending = history.Take(keepWindowStart)
            .Where(m => m.SequenceNumber > session.SummarizedThroughSequenceNumber)
            .ToList();
        if (pending.Count == 0) return true; // already fully compacted up to the keep window

        var pendingChars = pending.Sum(EffectiveContentLength);
        if (pending.Count < agentSettings.Value.CompactionBatchSize && pendingChars < agentSettings.Value.CompactionMaxCharsPerCall)
            return true; // below both triggers — pending messages are sent raw this turn instead

        return (await FoldIntoSummaryAsync(session, pending, ct)).Folded > 0;
    }

    public async IAsyncEnumerable<AgentStreamEvent> CompactHistoryAsync(Guid sessionId, [EnumeratorCancellation] CancellationToken ct)
    {
        var session = await repo.GetSessionAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");

        var history = await repo.GetMessagesAsync(sessionId, ct);
        yield return new TurnStartedEvent(history.Count > 0 ? history[^1].SequenceNumber : 0);

        // Unlike automatic compaction there's no kept window and no trigger threshold: everything not yet
        // summarized is folded, in as many CompactionMaxCharsPerCall-bounded passes as it takes.
        var totalFolded = 0;
        while (true)
        {
            // Context-excluded messages (e.g. an earlier compaction marker) carry nothing to summarize;
            // skipping them keeps compacting an already-compacted chat a no-op instead of a wasted call.
            var pending = history
                .Where(m => m.SequenceNumber > session.SummarizedThroughSequenceNumber && !m.ExcludeFromContext)
                .ToList();
            if (pending.Count == 0) break;

            var (folded, totals) = await FoldIntoSummaryAsync(session, pending, ct);
            if (totals is { } t)
                yield return new TokenUsageEvent(t.InputTokens, t.OutputTokens);
            if (folded == 0)
                throw new InvalidOperationException(totalFolded == 0
                    ? "Compaction failed: the model did not return a summary."
                    : $"Compaction stopped partway: {totalFolded} message(s) were summarized, but the model did not return a summary for the rest.");
            totalFolded += folded;
        }

        if (totalFolded > 0)
            await repo.AppendMessageAsync(new ChatMessage
            {
                SessionId = sessionId,
                Role = MessageRole.System,
                Content = $"{CompactionMarkerPrefix} — {totalFolded} earlier message(s) summarized.",
                ExcludeFromContext = true
            }, ct);

        yield return new CompactionCompletedEvent(totalFolded);
        yield return new DoneEvent();
    }

    /// <summary>Start of the UI-only System message a manual compaction leaves in the transcript.</summary>
    public const string CompactionMarkerPrefix = "Conversation compacted";

    /// <summary>Folds the oldest messages of <paramref name="pending"/> (bounded by CompactionMaxCharsPerCall,
    /// never splitting a tool call from its result) into session.HistorySummary with one provider call, and
    /// persists the result. Returns how many messages were folded — 0 when the call failed or returned
    /// nothing, leaving the summary and cursor untouched — plus the session's new token totals when the
    /// provider reported usage.</summary>
    private async Task<(int Folded, (long InputTokens, long OutputTokens)? Totals)> FoldIntoSummaryAsync(
        ChatSession session, IReadOnlyList<ChatMessage> pending, CancellationToken ct)
    {
        var budget = agentSettings.Value.CompactionMaxCharsPerCall;
        var toFold = new List<ChatMessage>();
        var used = 0;
        foreach (var m in pending)
        {
            var len = EffectiveContentLength(m);
            if (toFold.Count > 0 && used + len > budget) break; // always fold at least one message so a single oversized message can't stall progress forever
            toFold.Add(m);
            used += len;
        }

        var safeCount = SafeFoldBoundary(pending, toFold.Count);
        if (safeCount != toFold.Count) toFold = pending.Take(safeCount).ToList();

        var summaryMessages = new List<AiChatMessage>
        {
            new("system",
                "You maintain a compact, factual running summary of an ongoing agent conversation. Given " +
                "the previous summary (if any) and a batch of new messages, produce ONE updated summary as " +
                "terse bullet points. Preserve: the user's goals/requests, decisions made, established " +
                "facts, file paths touched, and tool-call outcomes (success/failure plus the key result). " +
                "Do not write prose or a narrative recap — bullets only. Merge the new messages into the " +
                "previous summary rather than appending a separate section."),
            new("user", $"Previous summary:\n{session.HistorySummary ?? "(none)"}\n\nMessages to fold in:\n{FormatForSummary(toFold)}")
        };

        var provider = providers.Resolve(session.ProviderKey);
        var summaryBuilder = new StringBuilder();
        AiTokenUsage? usage = null;
        try
        {
            await foreach (var chunk in provider.ChatStreamAsync(session.ModelName, summaryMessages, [], session.NumCtx, think: null, reasoningEffort: null, ct))
            {
                if (!string.IsNullOrEmpty(chunk.ContentDelta))
                    summaryBuilder.Append(chunk.ContentDelta);
                if (chunk.Usage is not null)
                    usage = chunk.Usage;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return (0, null);
        }

        // Real provider cost even though it isn't one of the turn's counted LLM calls; during a chat turn
        // the next main-call TokenUsageEvent carries it to the client.
        (long, long)? totals = null;
        if (usage is not null)
            totals = await repo.AddTokenUsageAsync(session.Id, usage, ct);

        var newSummary = summaryBuilder.ToString().Trim();
        if (newSummary.Length == 0) return (0, totals);

        // Each pass threads the entire previous summary back into the next summarization prompt with
        // no shrinkage of its own, so an imperfectly-compressing model could otherwise let HistorySummary
        // grow unboundedly over a long-lived session until it alone approaches the context window this
        // feature exists to protect. Hard-cap it defensively — losing the oldest, least-recent bullet
        // points to truncation is still strictly better than the summary itself causing an overflow.
        if (newSummary.Length > agentSettings.Value.CompactionMaxSummaryChars)
            newSummary = newSummary[..agentSettings.Value.CompactionMaxSummaryChars] +
                $"\n...[earlier summary truncated, {newSummary.Length - agentSettings.Value.CompactionMaxSummaryChars} characters dropped]";

        var throughSequenceNumber = toFold[^1].SequenceNumber;
        session.HistorySummary = newSummary;
        session.SummarizedThroughSequenceNumber = throughSequenceNumber;
        await repo.UpdateHistoryCompactionAsync(session.Id, newSummary, throughSequenceNumber, ct);
        return (toFold.Count, totals);
    }

    private int EffectiveContentLength(ChatMessage m)
    {
        var len = m.Content?.Length ?? 0;
        return m.Role == MessageRole.Tool ? Math.Min(len, agentSettings.Value.ToolResultMaxChars) : len;
    }

    /// <summary>Grows a candidate fold count so the first message left in <paramref name="pending"/>
    /// after folding is never an orphaned Tool-role message — one whose requesting Assistant tool-call
    /// message was folded into the summary while its own result stayed raw. Always extends *forward*
    /// (pulling the paired Tool result(s) into the fold) rather than shrinking backward: shrinking back
    /// to 0 would defer this pass entirely, and since <paramref name="pending"/> is rescanned from the
    /// same starting point every pass — nothing ages the oldest messages out — a session whose oldest
    /// pending message is a large Assistant/tool-call pair would then never compact again, growing
    /// unboundedly. Extending forward may pull the fold slightly over CompactionMaxCharsPerCall's
    /// budget for one pass, but guarantees progress every time.</summary>
    private static int SafeFoldBoundary(IReadOnlyList<ChatMessage> pending, int candidateCount)
    {
        var boundary = candidateCount;
        while (boundary < pending.Count && pending[boundary].Role == MessageRole.Tool)
            boundary++;
        return boundary;
    }

    private string FormatForSummary(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages.Where(m => !m.ExcludeFromContext))
        {
            var label = m.Role == MessageRole.Tool && !string.IsNullOrEmpty(m.ToolName) ? $"tool:{m.ToolName}" : m.Role.ToString().ToLowerInvariant();
            var content = m.Role == MessageRole.Tool ? CapContent(m.Content, agentSettings.Value.ToolResultMaxChars) : m.Content;

            // An Assistant message that only made tool calls typically has empty Content — the actual
            // call (name + arguments) lives in ToolCallsJson instead, which the raw wire payload sends
            // as a separate structured field (see BuildWireMessages) but this flattened transcript would
            // otherwise drop entirely, leaving the summarizer unable to say what file/query/command a
            // tool call was actually for.
            if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ToolCallsJson))
            {
                var callsText = SummarizeToolCalls(m.ToolCallsJson);
                if (!string.IsNullOrEmpty(callsText))
                    content = string.IsNullOrEmpty(content) ? callsText : $"{content}\n{callsText}";
            }

            sb.Append(label).Append(": ").AppendLine(content);
        }
        return sb.ToString();
    }

    private static string SummarizeToolCalls(string toolCallsJson)
    {
        List<AiToolCall>? calls;
        try
        {
            calls = JsonSerializer.Deserialize<List<AiToolCall>>(toolCallsJson);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        return calls is not { Count: > 0 }
            ? string.Empty
            : string.Join("\n", calls.Select(c => $"[called {c.Name}({c.ArgumentsJson})]"));
    }

    private static string? CapContent(string? content, int maxChars)
    {
        if (content is null || content.Length <= maxChars) return content;
        return content[..maxChars] + $"\n...[{content.Length - maxChars} characters truncated]";
    }

    /// <summary>True when the text trails off into an announcement of a next step —
    /// ends with a colon, possibly wrapped in markdown emphasis ("**Edit response:**").</summary>
    private static bool LooksLikeUnfinishedAnnouncement(string text)
    {
        var trimmed = text.TrimEnd().TrimEnd('*', '_', '`').TrimEnd();
        return trimmed.EndsWith(':');
    }

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline >= 0 ? text[..newline].TrimEnd() : text;
    }
}
