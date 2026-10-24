using PersonalAI.Api.Streaming;
using PersonalAI.Core.Models.DTOs;
using Microsoft.AspNetCore.Mvc;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.Sessions;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public class SessionsController(ChatSessionsService sessions, ChatTurnRunner turns) : ControllerBase
{
    [HttpGet]
    public async Task<SessionPageDto> GetSessions(
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = ChatSessionsService.DefaultPageSize, CancellationToken ct = default)
    {
        return await sessions.GetSessions(search, page, pageSize, ct);
    }

    [HttpPatch("{id:guid}/title")]
    public async Task<SessionSummaryDto> RenameSession(Guid id, RenameSessionRequest request, CancellationToken ct)
    {
        return await sessions.Rename(id, request, ct);
    }

    [HttpPost]
    public async Task<ActionResult<SessionSummaryDto>> CreateSession(CreateSessionRequest request, CancellationToken ct)
    {
        var session = await sessions.Create(request, ct);
        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, session);
    }

    [HttpGet("{id:guid}")]
    public async Task<SessionDetailDto> GetSession(Guid id, CancellationToken ct)
    {
        return await sessions.GetSession(id, ct);
    }

    [HttpPatch("{id:guid}/settings")]
    public async Task<SessionSettingsDto> UpdateSessionSettings(Guid id, UpdateSessionSettingsRequest request, CancellationToken ct)
    {
        return await sessions.UpdateSettings(id, request, ct);
    }

    /// <summary>Starts a turn in the background and streams it (SSE). The turn is owned by
    /// <see cref="ChatTurnRunner"/>, not this request: closing the connection doesn't stop it — use
    /// POST turn/stop for that, and GET turn/stream to re-attach.</summary>
    [HttpPost("{id:guid}/messages")]
    public async Task SendMessage(Guid id, SendMessageRequest request, CancellationToken ct)
    {
        await SseEventWriter.WriteTurnAsync(Response, await sessions.StartMessage(id, request.Text, ct), ct);
    }

    /// <summary>Summarizes the conversation so far into the session's history summary (SSE).</summary>
    [HttpPost("{id:guid}/compact")]
    public async Task CompactHistory(Guid id, CancellationToken ct)
    {
        await SseEventWriter.WriteTurnAsync(Response, await sessions.StartCompaction(id, ct), ct);
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
    public IActionResult StopActiveTurn(Guid id)
    {
        return turns.TryStop(id) ? NoContent() : NotFound();
    }
}
