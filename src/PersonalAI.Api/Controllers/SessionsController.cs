using System.Text.Json;
using PersonalAI.Api.Streaming;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.AI;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public class SessionsController(IChatSessionRepository repo, IChatTurnRunner turns, AIProviderRegistry providers) : ControllerBase
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const int MaxTitleLength = 200;

    [HttpGet]
    public async Task<ActionResult<SessionPageDto>> GetSessions(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = DefaultPageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (items, totalCount) = await repo.GetSessionsAsync(search, page, pageSize, ct);
        return Ok(new SessionPageDto(items.Select(ToSummaryDto).ToList(), totalCount, page, pageSize));
    }

    [HttpPatch("{id:guid}/title")]
    public async Task<ActionResult<SessionSummaryDto>> RenameSession(Guid id, RenameSessionRequest request, CancellationToken ct)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title))
            return BadRequest("title is required.");
        if (title.Length > MaxTitleLength)
            return BadRequest($"title must be at most {MaxTitleLength} characters.");

        var session = await repo.SetSessionTitleAsync(id, title, ct);
        if (session is null) return NotFound();
        return Ok(ToSummaryDto(session));
    }

    [HttpPost]
    public async Task<ActionResult<SessionSummaryDto>> CreateSession(CreateSessionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModelName))
            return BadRequest("modelName is required.");
        if (!ModelSettings.TryNormalizeReasoningEffort(request.ReasoningEffort, out var reasoningEffort))
            return BadRequest(ModelSettings.InvalidReasoningEffortMessage(request.ReasoningEffort));

        var session = await repo.CreateSessionAsync(
            request.Title, request.ModelName, ct, request.ProviderKey,
            request.ToolsEnabled, request.NumCtx, request.TrimHistoryEnabled, request.ThinkingEnabled,
            request.MaxLlmCalls, request.EnabledSkillIds, request.ToolFlags, reasoningEffort);
        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, ToSummaryDto(session));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SessionDetailDto>> GetSession(Guid id, CancellationToken ct)
    {
        var session = await repo.GetSessionAsync(id, ct);
        if (session is null) return NotFound();
        return Ok(ToDetailDto(session, turns.GetActive(id) is not null));
    }

    [HttpPatch("{id:guid}/settings")]
    public async Task<ActionResult<SessionSettingsDto>> UpdateSessionSettings(Guid id, UpdateSessionSettingsRequest request, CancellationToken ct)
    {
        if (!ModelSettings.TryNormalizeReasoningEffort(request.ReasoningEffort, out var reasoningEffort))
            return BadRequest(ModelSettings.InvalidReasoningEffortMessage(request.ReasoningEffort));

        var providerKey = request.ProviderKey?.Trim();
        var modelName = request.ModelName?.Trim();
        if (providerKey is not null && !providers.TryResolve(providerKey, out _))
            return BadRequest($"Unknown AI provider '{providerKey}'.");
        if (modelName is not null && modelName.Length == 0)
            return BadRequest("modelName cannot be empty.");
        // Switching provider without naming a model would pair the old model name with a provider that likely
        // doesn't have it.
        if (providerKey is not null && modelName is null)
            return BadRequest("modelName is required when changing providerKey.");

        // Clients send the model only when changing it, so any model here during a running turn is a mid-reply switch.
        if (modelName is not null && turns.GetActive(id) is not null)
            return Conflict("A response is being generated in this chat. Stop it or wait for it to finish before switching models.");

        var session = await repo.UpdateSessionSettingsAsync(id, request.ToolsEnabled, request.NumCtx, request.TrimHistoryEnabled, request.ThinkingEnabled, reasoningEffort, request.MaxLlmCalls, request.EnabledSkillIds ?? [], request.ToolFlags, ct, providerKey, modelName);
        if (session is null) return NotFound();

        return Ok(new SessionSettingsDto(session.Id, session.ProviderKey, session.ModelName, session.ToolsEnabled, session.NumCtx, session.TrimHistoryEnabled, session.ThinkingEnabled, session.ReasoningEffort, session.MaxLlmCalls, ToEnabledSkillIds(session), ToToolFlags(session)));
    }

    /// <summary>Starts a turn in the background and streams it (SSE). The turn is owned by
    /// <see cref="IChatTurnRunner"/>, not this request: closing the connection doesn't stop it — use
    /// POST turn/stop for that, and GET turn/stream to re-attach.</summary>
    [HttpPost("{id:guid}/messages")]
    public async Task<IActionResult> SendMessage(Guid id, SendMessageRequest request, CancellationToken ct)
    {
        if (!await repo.SessionExistsAsync(id, ct)) return NotFound();

        var turn = turns.TryStart(id, (agent, turnCt) => agent.RunTurnAsync(id, request.Text, turnCt));
        if (turn is null) return Conflict("A response is already being generated in this chat. Stop it or wait for it to finish.");

        await SseEventWriter.WriteTurnAsync(Response, turn, ct);
        return new EmptyResult();
    }

    /// <summary>Summarizes the conversation so far into the session's history summary (SSE). Runs as a turn, so it
    /// can't overlap a response being generated and can be stopped / re-attached like one.</summary>
    [HttpPost("{id:guid}/compact")]
    public async Task<IActionResult> CompactHistory(Guid id, CancellationToken ct)
    {
        if (!await repo.SessionExistsAsync(id, ct)) return NotFound();
        if (await repo.HasPendingApprovalsAsync(id, ct))
            return Conflict("Resolve the pending tool approvals before compacting this chat.");

        var turn = turns.TryStart(id, (agent, turnCt) => agent.CompactHistoryAsync(id, turnCt));
        if (turn is null) return Conflict("A response is already being generated in this chat. Stop it or wait for it to finish.");

        await SseEventWriter.WriteTurnAsync(Response, turn, ct);
        return new EmptyResult();
    }

    /// <summary>Re-attaches to the session's running turn: replays its events so far, then streams live.
    /// 204 when nothing is running.</summary>
    [HttpGet("{id:guid}/turn/stream")]
    public async Task<IActionResult> StreamActiveTurn(Guid id, CancellationToken ct)
    {
        var turn = turns.GetActive(id);
        if (turn is null) return NoContent();

        await SseEventWriter.WriteTurnAsync(Response, turn, ct);
        return new EmptyResult();
    }

    /// <summary>Stops the session's running turn (204). Its stream ends shortly after with a "done" event, once the
    /// interruption has been recorded in the transcript. 404 when nothing is running.</summary>
    [HttpPost("{id:guid}/turn/stop")]
    public IActionResult StopActiveTurn(Guid id) => turns.TryStop(id) ? NoContent() : NotFound();

    private static SessionSummaryDto ToSummaryDto(ChatSession s) =>
        new(s.Id, s.Title, s.ProviderKey, s.ModelName, s.Status, s.CreatedAt, s.UpdatedAt);

    private static SessionDetailDto ToDetailDto(ChatSession s, bool isTurnRunning) => new(
        s.Id,
        s.Title,
        s.ProviderKey,
        s.ModelName,
        s.SystemPrompt,
        s.Status,
        s.CreatedAt,
        s.UpdatedAt,
        s.ToolsEnabled,
        s.NumCtx,
        s.TrimHistoryEnabled,
        s.ThinkingEnabled,
        s.ReasoningEffort,
        s.MaxLlmCalls,
        s.TotalLlmCalls,
        s.TotalInputTokens,
        s.TotalOutputTokens,
        ToEnabledSkillIds(s),
        ToToolFlags(s),
        s.Messages.Select(ToMessageDto).ToList(),
        s.ToolInvocations
            .Where(t => t.Status == ToolInvocationStatus.PendingApproval)
            .Select(ToToolInvocationDto)
            .ToList(),
        isTurnRunning);

    private static MessageDto ToMessageDto(ChatMessage m) => new(
        m.Id,
        m.SequenceNumber,
        m.Role,
        m.Content,
        string.IsNullOrEmpty(m.ToolCallsJson) ? null : JsonSerializer.Deserialize<List<ToolCallDto>>(m.ToolCallsJson),
        m.ToolCallId,
        m.ToolName,
        m.IsError,
        m.CreatedAt);

    private static ToolInvocationDto ToToolInvocationDto(ToolInvocation t) => new(
        t.Id, t.ToolName, t.ArgumentsJson, t.Status, t.ResolvedPath, t.Reason, t.ResultContent, t.IsError);

    private static List<Guid> ToEnabledSkillIds(ChatSession s) =>
        JsonSerializer.Deserialize<List<Guid>>(s.EnabledSkillIdsJson) ?? [];

    private static ToolFlags ToToolFlags(ChatSession s) => new(
        s.WebSearchEnabled, s.ReadFileEnabled, s.WriteFileEnabled, s.EditFileEnabled, s.ListFilesEnabled, s.SearchFilesEnabled, s.NetworkScanEnabled, s.HttpRequestEnabled, s.ComputerControlEnabled, s.SandboxEnabled, s.GmailEnabled);
}
