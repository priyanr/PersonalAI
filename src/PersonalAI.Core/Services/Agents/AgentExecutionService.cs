using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Agents.Types;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Scheduling;
using PersonalAI.Core.Services.Skills;
using PersonalAI.Core.Tools;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.Agents;

/// <summary>Executes an Agent's prompt through its own short agentic loop, same shape as
/// SkillExecutionService's, except the "tools" it exposes to the model are the agent's allowed Skills —
/// each invoked via ISkillExecutionService and folded back in as a single tool result — plus any Tools
/// the agent is allowed to call directly, executed the same restricted way SkillExecutionService runs a
/// tool: no user is present to approve a workspace-escape, so ToolApprovalRequired is rejected instead of
/// pausing. Every run is recorded as an AgentRun (created Running before the first provider call,
/// finalized when the loop ends or fails) regardless of whether the caller — the SSE controller for a
/// manual run, or the scheduler for an unattended one — consumes the whole stream.</summary>
public sealed class AgentExecutionService(
    IAgentsRepository agentsRepo,
    IAgentRunsRepository runsRepo,
    ISkillsRepository skillsRepo,
    ISkillExecutionService skillExecution,
    AIProviderRegistry providers,
    ToolRegistry tools,
    IOptions<AgentSettings> agentSettings,
    ISandboxHostClient sandboxHostClient,
    AgentTypeRegistry agentTypes,
    ILogger<AgentExecutionService> logger,
    Monitoring.Collectors.DataSourceRegistry? dataSources = null) : IAgentExecutionService
{
    /// <summary>What a run (or a follow-up on a finished run) needs that does not depend on the run itself.</summary>
    private sealed record RunContext(
        PersonalAI.Core.Models.Entities.Agent Agent,
        Dictionary<string, Guid> ToolNameToSkillId,
        List<AiToolDefinition> ToolDefinitions,
        HashSet<string> EnabledDirectTools,
        int SkillAndToolCount,
        IAIProvider Provider);

    /// <summary>How a finished run looked before a follow-up reopened it. A follow-up is a conversation on that run, not a new
    /// execution, so when it ends the run goes back to this status, summary and completion time; what the follow-up did (or
    /// how it failed) is in the transcript.</summary>
    private sealed record FollowUpOrigin(AgentRunStatus Status, string? Summary, DateTimeOffset? CompletedAt);

    // One follow-up at a time per run, even across simultaneous requests (a status check alone is a read-then-write race).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> ActiveFollowUps = new();

    private const int DigestEntryLimit = 1200;

    private const string FollowUpNote =
        "\n\n[Follow-up] The run in the conversation above has finished and the user is now talking to you in the same run. " +
        "Do what they ask, using your tools when it needs them (for example to add something the dashboard is missing, " +
        "or a collection job); otherwise just answer. Reply with plain text when done.";

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(Guid agentId, AgentRunTrigger trigger, [EnumeratorCancellation] CancellationToken ct)
    {
        var context = await PrepareAsync(agentId, ct);
        var run = await runsRepo.CreateAsync(agentId, trigger, ct);
        var messages = new List<AiChatMessage> { new("system", ""), new("user", "Run this agent.") };

        await foreach (var evt in LoopAsync(context, run.Id, messages, [], followUp: null, ct))
            yield return evt;
    }

    /// <summary>Continues a finished run with a message from the user: the same agent, tools and sandbox owner, with the
    /// run's transcript as the conversation so far. The reply is appended to the run's transcript; the agent's schedule
    /// is left alone.</summary>
    public async IAsyncEnumerable<AgentStreamEvent> ContinueAsync(Guid agentId, Guid runId, string message, [EnumeratorCancellation] CancellationToken ct)
    {
        var run = await runsRepo.GetAsync(runId, ct);
        if (run is null || run.AgentId != agentId) throw new InvalidOperationException($"Run {runId} not found.");
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("A message is required.");

        if (!ActiveFollowUps.TryAdd(runId, 0)) throw new InvalidOperationException("This run is still in progress.");
        try
        {
            var context = await PrepareAsync(agentId, ct);
            var transcript = JsonSerializer.Deserialize<List<AgentRunTranscriptEntry>>(run.TranscriptJson) ?? [];
            transcript.Add(new AgentRunTranscriptEntry("user", null, message.Trim(), false));

            var origin = new FollowUpOrigin(run.Status, run.Summary, run.CompletedAt);
            if (!await runsRepo.ReopenAsync(runId, JsonSerializer.Serialize(transcript), ct))
                throw new InvalidOperationException("This run is still in progress.");

            var messages = new List<AiChatMessage> { new("system", ""), new("user", "Run this agent.") };
            messages.AddRange(RebuildHistory(transcript));

            await foreach (var evt in LoopAsync(context, runId, messages, transcript, origin, ct))
                yield return evt;
        }
        finally
        {
            ActiveFollowUps.TryRemove(runId, out _);
        }
    }

    /// <summary>The transcript as chat messages. Tool calls were not stored with their provider call ids, so tool
    /// activity is folded into a short assistant digest instead of tool messages providers would reject as orphans.
    /// Adjacent messages of one role are merged.</summary>
    private static List<AiChatMessage> RebuildHistory(List<AgentRunTranscriptEntry> transcript)
    {
        var result = new List<AiChatMessage>();
        void Add(string role, string content)
        {
            if (result.Count > 0 && result[^1].Role == role)
                result[^1] = result[^1] with { Content = result[^1].Content + "\n\n" + content };
            else
                result.Add(new AiChatMessage(role, content));
        }

        var digest = new StringBuilder();
        void FlushDigest()
        {
            if (digest.Length == 0) return;
            Add("assistant", "[Tool activity earlier in this run]\n" + digest.ToString().TrimEnd());
            digest.Clear();
        }

        foreach (var entry in transcript)
        {
            if (entry.Kind is "tool" or "skill")
            {
                var content = entry.Content ?? "";
                if (content.Length > DigestEntryLimit) content = content[..DigestEntryLimit] + " ...(truncated)";
                digest.Append("- ").Append(entry.SkillName).Append(entry.IsError ? " (failed): " : ": ").AppendLine(content);
                continue;
            }

            FlushDigest();
            if (!string.IsNullOrWhiteSpace(entry.Content))
                Add(entry.Kind == "user" ? "user" : "assistant", entry.Content);
        }
        FlushDigest();
        return result;
    }

    private async Task<RunContext> PrepareAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await agentsRepo.GetAsync(agentId, ct)
            ?? throw new InvalidOperationException($"Agent {agentId} not found.");

        var enabledSkillIds = new HashSet<Guid>(JsonSerializer.Deserialize<List<Guid>>(agent.EnabledSkillIdsJson) ?? []);
        var enabledSkills = (await skillsRepo.GetAllAsync(ct)).Where(s => enabledSkillIds.Contains(s.Id)).ToList();

        var toolNameToSkillId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var toolDefinitions = new List<AiToolDefinition>();
        foreach (var skill in enabledSkills)
        {
            var toolName = SkillToolCatalog.BuildUniqueToolName(skill.Name, toolNameToSkillId.Keys);
            toolNameToSkillId[toolName] = skill.Id;
            toolDefinitions.Add(new AiToolDefinition(
                toolName,
                string.IsNullOrWhiteSpace(skill.Description) ? $"Runs the '{skill.Name}' skill." : skill.Description,
                SkillToolCatalog.ParametersSchema));
        }

        // Direct tools use their own real names (e.g. "gmail"), which never collide with the
        // "skill_"-prefixed names above, so both can share one dispatch table by tool call name.
        var enabledDirectTools = new HashSet<string>(
            JsonSerializer.Deserialize<List<string>>(agent.EnabledToolsJson) ?? [], StringComparer.OrdinalIgnoreCase);
        // The tools a type grants: its required ones, plus (for Dashboard & Alerts) one per data source in the agent's
        // scope. A scope key only counts if it names a registered data source: it is user-editable JSON, and must never
        // be a way to switch on an unrelated tool (computer_control, sandbox, gmail, write_file...).
        var descriptor = agentTypes.Get(agent.Type);
        var required = descriptor.RequiredTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in descriptor.ToolsFor(agent.TypeConfigJson))
            if (required.Contains(name) || dataSources?.Find(name) is not null) enabledDirectTools.Add(name);
        toolDefinitions.AddRange(tools.Definitions.Where(d => enabledDirectTools.Contains(d.Name)));

        return new RunContext(agent, toolNameToSkillId, toolDefinitions, enabledDirectTools,
            enabledSkills.Count + enabledDirectTools.Count, providers.Resolve(agent.ProviderKey));
    }

    private async IAsyncEnumerable<AgentStreamEvent> LoopAsync(
        RunContext context, Guid runId, List<AiChatMessage> messages, List<AgentRunTranscriptEntry> transcript, FollowUpOrigin? followUp,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (agent, toolNameToSkillId, toolDefinitions, enabledDirectTools, skillAndToolCount, provider) = context;
        var agentId = agent.Id;

        // Everything from here on runs against this run's own ephemeral sandbox (SandboxOwnerKind.AgentRun,
        // runId) — torn down in the finally below regardless of how the run ends (success, failure,
        // cancellation). Nothing before this point could have created a sandbox, since runId didn't exist yet.
        try
        {
            yield return new AgentRunStartedEvent(runId);

            var basePrompt = BuildSystemPrompt(agent, skillAndToolCount) + (followUp is not null ? FollowUpNote : "");
            var maxIterations = agent.MaxLlmCalls ?? agentSettings.Value.MaxIterations;
            var failedCalls = new Dictionary<(string Name, string Args), string>();

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                // Relayed fresh each call (the system message is resent with the full history every
                // iteration) so the model can see its own budget and pace itself instead of being cut off
                // mid-task when the cap is hit.
                messages[0] = messages[0] with
                {
                    Content = basePrompt + $"\n\n[Call budget] This is LLM call {iteration + 1} of {maxIterations} for this run. " +
                        "Finish and respond with plain text (no further tool calls) before you exceed this budget."
                };

                var contentBuilder = new StringBuilder();
                var reasoningBuilder = new StringBuilder();
                IReadOnlyList<AiToolCall>? toolCalls = null;
                AiTokenUsage? usage = null;
                var hitOutputLimit = false;
                string? providerError = null;

                // Retry a transient network failure, only if nothing from the failed attempt was consumed
                // yet (otherwise a retry would duplicate streamed text or tool calls).
                yield return new LlmCallEvent(await runsRepo.IncrementLlmCallsAsync(runId, ct));
                var maxRetries = agentSettings.Value.ProviderRetryCount;
                for (var attempt = 0; attempt <= maxRetries; attempt++)
                {
                    providerError = null;
                    reasoningBuilder.Clear(); // a retried attempt starts its thinking over
                    Exception? transientError = null;
                    var enumerator = provider.ChatStreamAsync(agent.ModelName, messages, toolDefinitions, agent.NumCtx, think: null, agent.ReasoningEffort, ct)
                        .GetAsyncEnumerator(ct);
                    try
                    {
                        while (true)
                        {
                            bool moved;
                            try
                            {
                                moved = await enumerator.MoveNextAsync();
                            }
                            catch (OperationCanceledException)
                            {
                                await FinalizeAsync(runId, agentId, followUp, AgentRunStatus.Failed, "Run was cancelled.", transcript, CancellationToken.None);
                                throw;
                            }
                            catch (Exception ex)
                            {
                                providerError = ProviderErrors.Describe(ex);
                                if (attempt < maxRetries && contentBuilder.Length == 0 && toolCalls is null && ProviderErrors.IsTransient(ex))
                                    transientError = ex;
                                break;
                            }
                            if (!moved) break;

                            var chunk = enumerator.Current;
                            if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
                                reasoningBuilder.Append(chunk.ReasoningDelta);
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
                    }
                    finally
                    {
                        await enumerator.DisposeAsync();
                    }

                    if (transientError is null) break;
                    await Task.Delay(TimeSpan.FromSeconds(agentSettings.Value.ProviderRetryDelaySeconds), ct);
                }

                if (usage is not null)
                {
                    var (inputTokens, outputTokens) = await runsRepo.AddTokenUsageAsync(runId, usage, ct);
                    yield return new TokenUsageEvent(inputTokens, outputTokens);
                }

                if (providerError is not null)
                {
                    await FinalizeAsync(runId, agentId, followUp, AgentRunStatus.Failed, providerError, transcript, CancellationToken.None);
                    yield return new ErrorEvent(providerError);
                    yield break;
                }

                var assistantContent = contentBuilder.ToString();
                if (string.IsNullOrWhiteSpace(assistantContent) && toolCalls is not { Count: > 0 })
                {
                    // Not a successful run with a blank summary — see ModelReplyDiagnostics.
                    var emptyNotice = ModelReplyDiagnostics.EmptyReplyNotice(
                        agent.ProviderKey, hitOutputLimit, reasoningBuilder.Length > 0, usage, canDisableThinking: false);
                    await FinalizeAsync(runId, agentId, followUp, AgentRunStatus.Failed, emptyNotice, transcript, CancellationToken.None);
                    yield return new ErrorEvent(emptyNotice);
                    yield break;
                }

                messages.Add(new AiChatMessage("assistant", assistantContent, toolCalls,
                    ReasoningContent: toolCalls is { Count: > 0 } && reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null));
                if (!string.IsNullOrWhiteSpace(assistantContent))
                {
                    transcript.Add(new AgentRunTranscriptEntry("text", null, assistantContent, false));
                    await runsRepo.AppendTranscriptAsync(runId, JsonSerializer.Serialize(transcript), ct);
                }

                if (toolCalls is null || toolCalls.Count == 0)
                {
                    if (hitOutputLimit)
                    {
                        // The partial answer is already in the transcript; the run didn't finish it.
                        var cutOffNotice = ModelReplyDiagnostics.CutOffAnswerNotice(agent.ProviderKey, canDisableThinking: false);
                        await FinalizeAsync(runId, agentId, followUp, AgentRunStatus.Failed, cutOffNotice, transcript, CancellationToken.None);
                        yield return new ErrorEvent(cutOffNotice);
                        yield break;
                    }

                    await FinalizeAsync(runId, agentId, followUp, AgentRunStatus.Succeeded, assistantContent, transcript, ct);
                    yield return new DoneEvent();
                    yield break;
                }

                foreach (var call in toolCalls)
                {
                    ct.ThrowIfCancellationRequested();

                    if (ModelReplyDiagnostics.TruncatedToolCallError(call, toolCalls, hitOutputLimit) is { } truncatedError)
                    {
                        messages.Add(new AiChatMessage("tool", truncatedError, null, call.ToolCallId));
                        transcript.Add(new AgentRunTranscriptEntry("tool", call.Name, truncatedError, true));
                        await runsRepo.AppendTranscriptAsync(runId, JsonSerializer.Serialize(transcript), ct);
                        yield return new ToolResultEvent(Guid.NewGuid(), call.Name, truncatedError, true);
                        continue;
                    }

                    var key = (call.Name, call.ArgumentsJson);
                    if (failedCalls.TryGetValue(key, out var previousError))
                    {
                        var msg = $"You already made this exact call and it failed with: {previousError} Do not repeat it.";
                        messages.Add(new AiChatMessage("tool", msg, null, call.ToolCallId));
                        transcript.Add(new AgentRunTranscriptEntry("skill", call.Name, msg, true));
                        await runsRepo.AppendTranscriptAsync(runId, JsonSerializer.Serialize(transcript), ct);
                        yield return new ToolResultEvent(Guid.NewGuid(), call.Name, msg, true);
                        continue;
                    }

                    if (!toolNameToSkillId.TryGetValue(call.Name, out var skillId))
                    {
                        if (enabledDirectTools.Contains(call.Name) && tools.TryGet(call.Name, out var directTool))
                        {
                            var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentStreamEvent>();
                            var executionTask = ExecuteDirectToolAsync(directTool, call.ArgumentsJson, runId, agentId, msg => channel.Writer.TryWrite(new ToolProgressEvent(Guid.Empty, call.Name, msg)), ct);
                            while (true)
                            {
                                var finished = await Task.WhenAny(executionTask, channel.Reader.WaitToReadAsync(ct).AsTask());
                                if (finished == executionTask) break;
                                while (channel.Reader.TryRead(out var evt)) yield return evt;
                            }
                            channel.Writer.Complete();
                            while (channel.Reader.TryRead(out var evt)) yield return evt;

                            var (directResult, directFailed) = await executionTask;
                            messages.Add(new AiChatMessage("tool", directResult, null, call.ToolCallId));
                            transcript.Add(new AgentRunTranscriptEntry("tool", call.Name, directResult, directFailed));
                            await runsRepo.AppendTranscriptAsync(runId, JsonSerializer.Serialize(transcript), ct);
                            if (directFailed) failedCalls[key] = FirstLine(directResult);
                            else failedCalls.Clear(); // state changed — a previously failed call may now succeed
                            yield return new ToolResultEvent(Guid.Empty, call.Name, directResult, directFailed);
                            continue;
                        }

                        var errMsg = $"'{call.Name}' is not one of this agent's enabled skills or tools.";
                        failedCalls[key] = errMsg;
                        messages.Add(new AiChatMessage("tool", errMsg, null, call.ToolCallId));
                        transcript.Add(new AgentRunTranscriptEntry("skill", call.Name, errMsg, true));
                        await runsRepo.AppendTranscriptAsync(runId, JsonSerializer.Serialize(transcript), ct);
                        yield return new ToolResultEvent(Guid.NewGuid(), call.Name, errMsg, true);
                        continue;
                    }

                    var input = SkillToolCatalog.ExtractInputArg(call.ArgumentsJson);
                    var skillOutput = new StringBuilder();
                    var skillFailed = false;
                    try
                    {
                        await foreach (var evt in skillExecution.RunAsync(skillId, input, ct))
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

                    var resultContent = skillOutput.Length > 0
                        ? skillOutput.ToString()
                        : skillFailed ? "Skill run failed." : "Skill produced no output.";
                    messages.Add(new AiChatMessage("tool", resultContent, null, call.ToolCallId));
                    transcript.Add(new AgentRunTranscriptEntry("skill", call.Name, resultContent, skillFailed));
                    await runsRepo.AppendTranscriptAsync(runId, JsonSerializer.Serialize(transcript), ct);
                    if (skillFailed) failedCalls[key] = FirstLine(resultContent);
                    else failedCalls.Clear(); // state changed — a previously failed call may now succeed
                    yield return new ToolResultEvent(Guid.NewGuid(), call.Name, resultContent, skillFailed);
                }
            }

            var capMessage = $"Stopped after {maxIterations} tool iterations without a final answer.";
            await FinalizeAsync(runId, agentId, followUp, AgentRunStatus.Failed, capMessage, transcript, ct);
            yield return new ErrorEvent(capMessage);
        }
        finally
        {
            // Skipping entirely when no host is connected avoids exception noise on every run in
            // deployments that never enable the sandbox feature — if it were never connected, no
            // sandbox could have been created for this runId either.
            if (sandboxHostClient.IsConnected)
            {
                try
                {
                    await sandboxHostClient.DestroySandboxAsync(runId, deleteFiles: true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // A failed teardown must never mask the run's real outcome — this is a best-effort
                    // safety net; the reaper's EphemeralGraceMinutes sweep is the backstop if it fails.
                    logger.LogWarning(ex, "Failed to tear down ephemeral sandbox for agent run {RunId}.", runId);
                }
            }
        }
    }

    private async Task FinalizeAsync(
        Guid runId, Guid agentId, FollowUpOrigin? followUp, AgentRunStatus status, string? summary,
        List<AgentRunTranscriptEntry> transcript, CancellationToken ct)
    {
        if (followUp is not null)
        {
            // A follow-up is a conversation on a past run, not a scheduled execution: it must not move the schedule or turn a
            // run that succeeded into a failed one. A failure is recorded in the transcript instead.
            if (status == AgentRunStatus.Failed)
                transcript.Add(new AgentRunTranscriptEntry("text", null, summary, true));
            await runsRepo.CompleteAsync(runId, followUp.Status, followUp.Summary, JsonSerializer.Serialize(transcript), ct, followUp.CompletedAt);
            return;
        }

        await runsRepo.CompleteAsync(runId, status, summary, JsonSerializer.Serialize(transcript), ct);

        var now = DateTimeOffset.UtcNow;
        var agent = await agentsRepo.GetAsync(agentId, ct);
        var nextRunAt = agent is null ? null : ScheduleCalculator.ComputeNextRunAt(agent, now);
        await agentsRepo.SetRunOutcomeAsync(agentId, now, nextRunAt, ct);
    }

    private string BuildSystemPrompt(PersonalAI.Core.Models.Entities.Agent agent, int enabledSkillAndToolCount)
    {
        var preamble = agentTypes.Get(agent.Type).SystemPromptPreamble;
        var prompt = preamble is null ? agent.Prompt : $"{preamble}\n\n{agent.Prompt}";
        return enabledSkillAndToolCount == 0
            ? prompt + "\n\n[Skills/Tools] No skills or tools are enabled for this agent — respond directly, you have nothing to invoke."
            : prompt;
    }

    /// <summary>Executes a directly-enabled Tool the same restricted way SkillExecutionService does: no
    /// secret substitution (direct tools don't have a secret allowlist — wrap them in a Skill if they need
    /// one), and a ToolApprovalRequired result is rejected outright since there's no user to approve it
    /// during unattended execution.</summary>
    private static async Task<(string Content, bool IsError)> ExecuteDirectToolAsync(ITool tool, string argumentsJson, Guid runId, Guid agentId, Action<string>? onProgress, CancellationToken ct)
    {
        JsonElement arguments;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            arguments = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return ($"Tool arguments were not valid JSON: {ex.Message}.", true);
        }

        ToolExecutionResult result;
        try
        {
            result = await tool.ExecuteAsync(arguments,
                new ToolExecutionContext(PreApproved: false, Sandbox: new SandboxOwner(SandboxOwnerKind.AgentRun, runId), OnProgress: onProgress, AgentId: agentId), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ($"Tool execution failed: {ex.Message}", true);
        }

        return result switch
        {
            ToolApprovalRequired approvalRequired => (
                $"Tool '{tool.Name}' requires user approval for a path outside the workspace " +
                $"({approvalRequired.ResolvedPath}), which is not supported during unattended agent execution.", true),
            ToolCompleted completed => (completed.Content, completed.IsError),
            _ => ("Tool produced no result.", true)
        };
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var newline = text.IndexOf('\n');
        return newline >= 0 ? text[..newline].TrimEnd() : text;
    }
}
