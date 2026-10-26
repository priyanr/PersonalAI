using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Secrets;
using PersonalAI.Core.Tools;
using Microsoft.Extensions.Options;

namespace PersonalAI.Core.Services.Skills;

/// <summary>Runs a Skill: its prompt through a short agent loop with only the skill's tools and secrets. Nothing is
/// saved; the conversation lives for one run.</summary>
public sealed class SkillExecutionService(
    ISkillsRepository skillsRepo,
    AIProviderRegistry providers,
    ToolRegistry tools,
    ISecretsRepository secrets,
    IOptions<AgentSettings> agentSettings,
    ISandboxHostClient sandboxHostClient,
    ILogger<SkillExecutionService> logger)
{
    /// <summary>Runs the skill until the model answers or hits the call cap. No user is there to approve anything, so a
    /// tool call that needs approval is refused instead of pausing.</summary>
    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(Guid skillId, string? input, [EnumeratorCancellation] CancellationToken ct)
    {
        // Tools run in this run's own sandbox, which the finally below removes however the run ends.
        var runId = Guid.NewGuid();
        try
        {
            await foreach (var evt in RunCoreAsync(skillId, input, runId, ct))
                yield return evt;
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
                    logger.LogWarning(ex, "Failed to tear down ephemeral sandbox for skill run {RunId}.", runId);
                }
            }
        }
    }

    private async IAsyncEnumerable<AgentStreamEvent> RunCoreAsync(Guid skillId, string? input, Guid runId, [EnumeratorCancellation] CancellationToken ct)
    {
        var skill = await skillsRepo.GetAsync(skillId, ct)
            ?? throw new InvalidOperationException($"Skill {skillId} not found.");

        var enabledTools = new HashSet<string>(
            JsonSerializer.Deserialize<List<string>>(skill.EnabledToolsJson) ?? [], StringComparer.OrdinalIgnoreCase);
        var secretKeys = JsonSerializer.Deserialize<List<string>>(skill.SecretKeysJson) ?? [];
        var availableSecrets = await secrets.GetValuesAsync(secretKeys, ct);

        var provider = providers.Resolve(skill.ProviderKey);
        var toolDefinitions = tools.Definitions.Where(d => enabledTools.Contains(d.Name)).ToList();

        var messages = new List<AiChatMessage>
        {
            new("system", BuildSystemPrompt(skill.Prompt, secretKeys)),
            new("user", string.IsNullOrWhiteSpace(input) ? "Execute this skill." : input)
        };

        var maxIterations = agentSettings.Value.MaxIterations;
        var failedCalls = new Dictionary<(string Name, string Args), string>();

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            IReadOnlyList<AiToolCall>? toolCalls = null;
            AiTokenUsage? usage = null;
            var hitOutputLimit = false;

            await foreach (var chunk in provider.ChatStreamAsync(skill.ModelName, messages, toolDefinitions, skill.NumCtx, think: null, reasoningEffort: null, ct))
            {
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

            var assistantContent = contentBuilder.ToString();
            if (string.IsNullOrWhiteSpace(assistantContent) && toolCalls is not { Count: > 0 })
            {
                // A blank reply is a failure, not a finished skill with no output — see ModelReplyDiagnostics.
                yield return new ErrorEvent(ModelReplyDiagnostics.EmptyReplyNotice(
                    skill.ProviderKey, hitOutputLimit, reasoningBuilder.Length > 0, usage, canDisableThinking: false));
                yield break;
            }

            messages.Add(new AiChatMessage("assistant", assistantContent, toolCalls,
                ReasoningContent: toolCalls is { Count: > 0 } && reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null));

            if (toolCalls is null || toolCalls.Count == 0)
            {
                if (hitOutputLimit)
                {
                    yield return new ErrorEvent(ModelReplyDiagnostics.CutOffAnswerNotice(skill.ProviderKey, canDisableThinking: false));
                    yield break;
                }

                yield return new DoneEvent();
                yield break;
            }

            foreach (var call in toolCalls)
            {
                ct.ThrowIfCancellationRequested();

                if (ModelReplyDiagnostics.TruncatedToolCallError(call, toolCalls, hitOutputLimit) is { } truncatedError)
                {
                    messages.Add(new AiChatMessage("tool", truncatedError, null, call.ToolCallId));
                    yield return new ToolResultEvent(Guid.NewGuid(), call.Name, truncatedError, true);
                    continue;
                }

                var key = (call.Name, call.ArgumentsJson);
                if (failedCalls.TryGetValue(key, out var previousError))
                {
                    var msg = $"You already made this exact call and it failed with: {previousError} Do not repeat it.";
                    messages.Add(new AiChatMessage("tool", msg, null, call.ToolCallId));
                    yield return new ToolResultEvent(Guid.NewGuid(), call.Name, msg, true);
                    continue;
                }

                if (!enabledTools.Contains(call.Name) || !tools.TryGet(call.Name, out var tool))
                {
                    var errMsg = $"Tool '{call.Name}' is not enabled for this skill. Available tools: {string.Join(", ", enabledTools)}.";
                    failedCalls[key] = errMsg;
                    messages.Add(new AiChatMessage("tool", errMsg, null, call.ToolCallId));
                    yield return new ToolResultEvent(Guid.NewGuid(), call.Name, errMsg, true);
                    continue;
                }

                JsonElement arguments = default;
                string? argError = null;
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                    arguments = SecretSubstitution.Apply(doc.RootElement.Clone(), availableSecrets);
                }
                catch (JsonException ex)
                {
                    argError = $"Tool arguments were not valid JSON: {ex.Message}.";
                }

                if (argError is not null)
                {
                    failedCalls[key] = argError;
                    messages.Add(new AiChatMessage("tool", argError, null, call.ToolCallId));
                    yield return new ToolResultEvent(Guid.NewGuid(), call.Name, argError, true);
                    continue;
                }

                var (progress, execution) = ProgressStream.Run(async onProgress =>
                {
                    try
                    {
                        var res = await tool.ExecuteAsync(arguments,
                            new ToolExecutionContext(PreApproved: false, Sandbox: new SandboxOwner(SandboxOwnerKind.SkillRun, runId), OnProgress: onProgress), ct);
                        return (Result: res, Error: (string?)null);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return (Result: (ToolExecutionResult?)null, Error: $"Tool execution failed: {ex.Message}");
                    }
                }, msg => new ToolProgressEvent(Guid.Empty, call.Name, msg), ct);
                await foreach (var evt in progress) yield return evt;
                var (result, executionError) = await execution;

                if (executionError is not null)
                {
                    failedCalls[key] = executionError;
                    messages.Add(new AiChatMessage("tool", executionError, null, call.ToolCallId));
                    yield return new ToolResultEvent(Guid.NewGuid(), call.Name, executionError, true);
                    continue;
                }

                switch (result!)
                {
                    case ToolApprovalRequired approvalRequired:
                        var gatedMsg = $"Tool '{call.Name}' requires user approval for a path outside the workspace " +
                                       $"({approvalRequired.ResolvedPath}), which is not supported during unattended skill execution.";
                        failedCalls[key] = gatedMsg;
                        messages.Add(new AiChatMessage("tool", gatedMsg, null, call.ToolCallId));
                        yield return new ToolResultEvent(Guid.NewGuid(), call.Name, gatedMsg, true);
                        break;

                    case ToolCompleted completed:
                        messages.Add(new AiChatMessage("tool", completed.Content, null, call.ToolCallId));
                        if (completed.IsError)
                            failedCalls[key] = FirstLine(completed.Content);
                        else
                            failedCalls.Clear(); // state changed — a previously failed call may now succeed
                        yield return new ToolResultEvent(Guid.NewGuid(), call.Name, completed.Content, completed.IsError);
                        break;
                }
            }
        }

        yield return new ErrorEvent($"Stopped after {maxIterations} tool iterations without a final answer.");
    }

    /// <summary>Runs the skill to the end and folds it into one tool result, as the chat and agent loops show it to
    /// their model: the skill's text, and whether it failed.</summary>
    public async Task<(string Content, bool Failed)> RunAsToolAsync(Guid skillId, string? input, CancellationToken ct)
    {
        var output = new StringBuilder();
        var failed = false;
        try
        {
            await foreach (var evt in RunAsync(skillId, input, ct))
            {
                switch (evt)
                {
                    case TextDeltaEvent d:
                        output.Append(d.Text);
                        break;
                    case ToolResultEvent t when t.IsError:
                        failed = true;
                        break;
                    case ErrorEvent e:
                        failed = true;
                        if (output.Length > 0) output.Append('\n');
                        output.Append(e.Message);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            failed = true;
            output.Append(ex.Message);
        }

        var content = output.Length > 0 ? output.ToString() : failed ? "Skill run failed." : "Skill produced no output.";
        return (content, failed);
    }

    private static string BuildSystemPrompt(string prompt, IReadOnlyList<string> secretKeys)
    {
        if (secretKeys.Count == 0) return prompt;

        var placeholders = string.Join(", ", secretKeys.Select(k => $"{{{{secret.{k}}}}}"));
        return prompt +
            "\n\n[Secrets] You may reference the following placeholders inside tool call arguments: " +
            placeholders + ". Each is substituted with its real secret value immediately before the tool " +
            "runs — you will never see the actual value, so always use the placeholder token verbatim, never guess or fabricate a value.";
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var newline = text.IndexOf('\n');
        return newline >= 0 ? text[..newline].TrimEnd() : text;
    }
}
