using PersonalAI.Api.Streaming;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities.Enums;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/tool-invocations")]
public class ToolInvocationsController(IChatSessionRepository repo, IChatTurnRunner turns) : ControllerBase
{
    /// <summary>Resolves the approval and continues the turn in the background (same ownership model as
    /// SessionsController.SendMessage), streaming it to this response.</summary>
    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, ResolveToolInvocationRequest request, CancellationToken ct)
    {
        var invocation = await repo.GetToolInvocationAsync(id, ct);
        if (invocation is null) return NotFound();
        if (invocation.Status != ToolInvocationStatus.PendingApproval)
            return Conflict($"Tool invocation is not pending approval (status: {invocation.Status}).");

        var turn = turns.TryStart(invocation.SessionId, (agent, turnCt) => agent.ResolveApprovalAsync(id, request.Approved, turnCt));
        if (turn is null) return Conflict("A response is already being generated in this chat. Wait for it to finish.");

        await SseEventWriter.WriteTurnAsync(Response, turn, ct);
        return new EmptyResult();
    }
}
