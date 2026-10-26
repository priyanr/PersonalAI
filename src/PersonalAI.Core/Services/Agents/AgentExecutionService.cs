using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
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

/// <summary>Runs an Agent: a short, unattended agent loop whose tools are the agent's skills (each folded into one tool
/// result) and the tools it may call directly. No user is there to approve anything, so a call that needs approval is
/// refused. Every run is recorded as an AgentRun, whether or not the caller reads the whole stream.</summary>
public sealed class AgentExecutionService(
    AppDbContext db,
    IAgentsRepository agentsRepo,
    IAgentRunsRepository runsRepo,
    ISkillsRepository skillsRepo,
    SkillExecutionService skillExecution,
    AIProviderRegistry providers,
    ToolRegistry tools,
    IOptions<AgentSettings> agentSettings,
    ISandboxHostClient sandboxHostClient,
    AgentTypeRegistry agentTypes,
    ILogger<AgentExecutionService> logger,
    Monitoring.Collectors.DataSourceRegistry dataSources)
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

    /// <summary>Runs the agent once and records the run; the agent's LastRunAt and NextRunAt move on when it ends.</summary>
    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(Guid agentId, AgentRunTrigger trigger, [EnumeratorCancellation] CancellationToken ct)
    {
        var context = await PrepareAsync(agentId, ct);
        var run = new AgentRun { AgentId = agentId, Trigger = trigger };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var messages = new List<AiChatMessage> { new("system", ""), new("user", "Run this agent.") };

        await foreach (var evt in LoopAsync(context, run, messages, [], followUp: null, ct))
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
            if (run.Status == AgentRunStatus.Running) throw new InvalidOperationException("This run is still in progress.");
            run.Status = AgentRunStatus.Running;
            run.CompletedAt = null;
            run.TranscriptJson = JsonSerializer.Serialize(transcript);
            await db.SaveChangesAsync(ct);

            var messages = new List<AiChatMessage> { new("system", ""), new("user", "Run this agent.") };
            messages.AddRange(RebuildHistory(transcript));

            await foreach (var evt in LoopAsync(context, run, messages, transcript, origin, ct))
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
            if (required.Contains(name) || dataSources.Find(name) is not null) enabledDirectTools.Add(name);
        toolDefinitions.AddRange(tools.Definitions.Where(d => enabledDirectTools.Contains(d.Name)));

        return new RunContext(agent, toolNameToSkillId, toolDefinitions, enabledDirectTools,
            enabledSkills.Count + enabledDirectTools.Count, providers.Resolve(agent.ProviderKey));
    }

    private async IAsyncEnumerable<AgentStreamEvent> LoopAsync(
        RunContext context, AgentRun run, List<AiChatMessage> messages, List<AgentRunTranscriptEntry> transcript, FollowUpOrigin? followUp,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (agent, toolNameToSkillId, toolDefinitions, enabledDirectTools, skillAndToolCount, provider) = context;
        var agentId = agent.Id;
        var runId = run.Id;

        // Tools run in this run's own sandbox, which the finally below removes however the run ends.
        try
        {
            yield return new AgentRunStartedEvent(runId);

            var basePrompt = BuildSystemPrompt(agent, skillAndToolCount) + (followUp is not null ? FollowUpNote : "");
            var maxIterations = agent.MaxLlmCalls ?? agentSettings.Value.MaxIterations;
            var failedCalls = new Dictionary<(string Name, string Args), string>();

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                // The budget is restated on every call so the model can pace itself instead of being cut off mid-task.
                messages[0] = messages[0] with
                {
                    Content = basePrompt + $"\n\n[Call budget] This is LLM call {iteration + 1} of {maxIterations} for this run. " +
                        "Finish and respond with plain text (no further tool calls) before you exceed this budget."
                };

                run.LlmCallCount++;
                await db.SaveChangesAsync(ct);
                yield return new LlmCallEvent(run.LlmCallCount);

                // The reply's text streams to the caller as it arrives; the rest is known once the call is done.
                var (textEvents, replyTask) = ProgressStream.Run(
                    onText => CallModelAsync(provider, agent, messages, toolDefinitions, onText, ct),
                    text => new TextDeltaEvent(text), CancellationToken.None);
                await foreach (var evt in textEvents) yield return evt;
                ModelReply reply;
                try
                {
                    reply = await replyTask;
                }
                catch (OperationCanceledException)
                {
                    await FinalizeAsync(run, agent, followUp, AgentRunStatus.Failed, "Run was cancelled.", transcript, CancellationToken.None);
                    throw;
                }
                var (assistantContent, reasoning, toolCalls, usage, hitOutputLimit, providerError) = reply;

                if (usage is not null)
                {
                    run.TotalInputTokens += usage.InputTokens;
                    run.TotalOutputTokens += usage.OutputTokens;
                    await db.SaveChangesAsync(ct);
                    yield return new TokenUsageEvent(run.TotalInputTokens, run.TotalOutputTokens);
                }

                if (providerError is not null)
                {
                    await FinalizeAsync(run, agent, followUp, AgentRunStatus.Failed, providerError, transcript, CancellationToken.None);
                    yield return new ErrorEvent(providerError);
                    yield break;
                }

                if (string.IsNullOrWhiteSpace(assistantContent) && toolCalls is not { Count: > 0 })
                {
                    // Not a successful run with a blank summary — see ModelReplyDiagnostics.
                    var emptyNotice = ModelReplyDiagnostics.EmptyReplyNotice(
                        agent.ProviderKey, hitOutputLimit, reasoning.Length > 0, usage, canDisableThinking: false);
                    await FinalizeAsync(run, agent, followUp, AgentRunStatus.Failed, emptyNotice, transcript, CancellationToken.None);
                    yield return new ErrorEvent(emptyNotice);
                    yield break;
                }

                messages.Add(new AiChatMessage("assistant", assistantContent, toolCalls,
                    ReasoningContent: toolCalls is { Count: > 0 } && reasoning.Length > 0 ? reasoning : null));
                if (!string.IsNullOrWhiteSpace(assistantContent))
                {
                    transcript.Add(new AgentRunTranscriptEntry("text", null, assistantContent, false));
                    await SaveTranscriptAsync();
                }

                if (toolCalls is null || toolCalls.Count == 0)
                {
                    if (hitOutputLimit)
                    {
                        // The partial answer is already in the transcript; the run didn't finish it.
                        var cutOffNotice = ModelReplyDiagnostics.CutOffAnswerNotice(agent.ProviderKey, canDisableThinking: false);
                        await FinalizeAsync(run, agent, followUp, AgentRunStatus.Failed, cutOffNotice, transcript, CancellationToken.None);
                        yield return new ErrorEvent(cutOffNotice);
                        yield break;
                    }

                    await FinalizeAsync(run, agent, followUp, AgentRunStatus.Succeeded, assistantContent, transcript, ct);
                    yield return new DoneEvent();
                    yield break;
                }

                foreach (var call in toolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    var key = (call.Name, call.ArgumentsJson);

                    if (ModelReplyDiagnostics.TruncatedToolCallError(call, toolCalls, hitOutputLimit) is { } truncatedError)
                    {
                        yield return await AnswerAsync(call, "tool", truncatedError, true);
                        continue;
                    }
                    if (failedCalls.TryGetValue(key, out var previousError))
                    {
                        yield return await AnswerAsync(call, "skill", $"You already made this exact call and it failed with: {previousError} Do not repeat it.", true);
                        continue;
                    }

                    string content;
                    bool failed;
                    string kind;
                    if (toolNameToSkillId.TryGetValue(call.Name, out var skillId))
                    {
                        (content, failed) = await skillExecution.RunAsToolAsync(skillId, SkillToolCatalog.ExtractInputArg(call.ArgumentsJson), ct);
                        kind = "skill";
                    }
                    else if (enabledDirectTools.Contains(call.Name) && tools.TryGet(call.Name, out var directTool))
                    {
                        var (progress, execution) = ProgressStream.Run(
                            onProgress => ExecuteDirectToolAsync(directTool, call.ArgumentsJson, runId, agentId, onProgress, ct),
                            msg => new ToolProgressEvent(Guid.Empty, call.Name, msg), ct);
                        await foreach (var evt in progress) yield return evt;
                        (content, failed) = await execution;
                        kind = "tool";
                    }
                    else
                    {
                        var notEnabled = $"'{call.Name}' is not one of this agent's enabled skills or tools.";
                        failedCalls[key] = notEnabled;
                        yield return await AnswerAsync(call, "skill", notEnabled, true);
                        continue;
                    }

                    // A failure is remembered so the model can't repeat it; a success may have changed what would fail.
                    if (failed) failedCalls[key] = FirstLine(content);
                    else failedCalls.Clear();
                    yield return await AnswerAsync(call, kind, content, failed);
                }
            }

            var capMessage = $"Stopped after {maxIterations} tool iterations without a final answer.";
            await FinalizeAsync(run, agent, followUp, AgentRunStatus.Failed, capMessage, transcript, ct);
            yield return new ErrorEvent(capMessage);
        }
        finally
        {
            // Without a connected host no sandbox can exist for this run, so there is nothing to remove.
            if (sandboxHostClient.IsConnected)
            {
                try
                {
                    await sandboxHostClient.DestroySandboxAsync(runId, deleteFiles: true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Best effort: a failed teardown must not hide the run's outcome; the reaper removes it later.
                    logger.LogWarning(ex, "Failed to tear down ephemeral sandbox for agent run {RunId}.", runId);
                }
            }
        }

        // Saved after every step, so a viewer polling the run sees it stream in rather than only once it finishes.
        async Task SaveTranscriptAsync()
        {
            run.TranscriptJson = JsonSerializer.Serialize(transcript);
            await db.SaveChangesAsync(ct);
        }

        // Gives the model a tool call's result and records it in the run's transcript.
        async Task<ToolResultEvent> AnswerAsync(AiToolCall call, string kind, string content, bool isError)
        {
            messages.Add(new AiChatMessage("tool", content, null, call.ToolCallId));
            transcript.Add(new AgentRunTranscriptEntry(kind, call.Name, content, isError));
            await SaveTranscriptAsync();
            return new ToolResultEvent(Guid.NewGuid(), call.Name, content, isError);
        }
    }

    /// <summary>One model call. A transient network failure is retried, but only while nothing of the failed attempt has
    /// been streamed yet (a retry would otherwise repeat text). Streamed text goes to <paramref name="onText"/>; a failure
    /// that isn't retried is returned in Error.</summary>
    private async Task<ModelReply> CallModelAsync(
        IAIProvider provider, PersonalAI.Core.Models.Entities.Agent agent, List<AiChatMessage> messages, List<AiToolDefinition> toolDefinitions,
        Action<string> onText, CancellationToken ct)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        IReadOnlyList<AiToolCall>? toolCalls = null;
        AiTokenUsage? usage = null;
        var hitOutputLimit = false;

        for (var attempt = 0; ; attempt++)
        {
            reasoning.Clear(); // a retried attempt starts its thinking over
            try
            {
                await foreach (var chunk in provider.ChatStreamAsync(agent.ModelName, messages, toolDefinitions, agent.NumCtx, think: null, agent.ReasoningEffort, ct))
                {
                    if (!string.IsNullOrEmpty(chunk.ReasoningDelta)) reasoning.Append(chunk.ReasoningDelta);
                    if (!string.IsNullOrEmpty(chunk.ContentDelta))
                    {
                        content.Append(chunk.ContentDelta);
                        onText(chunk.ContentDelta);
                    }
                    if (chunk.ToolCalls is { Count: > 0 }) toolCalls = chunk.ToolCalls;
                    if (chunk.Usage is not null) usage = chunk.Usage;
                    if (chunk.HitOutputLimit) hitOutputLimit = true;
                }
                return new ModelReply(content.ToString(), reasoning.ToString(), toolCalls, usage, hitOutputLimit, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var retry = attempt < agentSettings.Value.ProviderRetryCount && content.Length == 0 && toolCalls is null && ProviderErrors.IsTransient(ex);
                if (!retry) return new ModelReply(content.ToString(), reasoning.ToString(), toolCalls, usage, hitOutputLimit, ProviderErrors.Describe(ex));
            }
            await Task.Delay(TimeSpan.FromSeconds(agentSettings.Value.ProviderRetryDelaySeconds), ct);
        }
    }

    private sealed record ModelReply(
        string Content, string Reasoning, IReadOnlyList<AiToolCall>? ToolCalls, AiTokenUsage? Usage, bool HitOutputLimit, string? Error);

    private async Task FinalizeAsync(
        AgentRun run, PersonalAI.Core.Models.Entities.Agent agent, FollowUpOrigin? followUp, AgentRunStatus status, string? summary,
        List<AgentRunTranscriptEntry> transcript, CancellationToken ct)
    {
        if (followUp is not null)
        {
            // A follow-up is a conversation on a past run, not a scheduled execution: it must not move the schedule or turn a
            // run that succeeded into a failed one. A failure is recorded in the transcript instead.
            if (status == AgentRunStatus.Failed)
                transcript.Add(new AgentRunTranscriptEntry("text", null, summary, true));
            run.TranscriptJson = JsonSerializer.Serialize(transcript);
            run.Status = followUp.Status;
            run.Summary = followUp.Summary;
            run.CompletedAt = followUp.CompletedAt;
            await db.SaveChangesAsync(ct);
            return;
        }

        // The run and the agent's schedule are saved together, so a finished run always moves the schedule on.
        var now = DateTimeOffset.UtcNow;
        run.TranscriptJson = JsonSerializer.Serialize(transcript);
        run.Status = status;
        run.Summary = summary;
        run.CompletedAt = now;
        agent.LastRunAt = now;
        agent.NextRunAt = ScheduleCalculator.ComputeNextRunAt(agent, now);
        await db.SaveChangesAsync(ct);
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
