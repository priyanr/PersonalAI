using PersonalAI.Api.Streaming;
using PersonalAI.Core.Models.DTOs;
using Microsoft.AspNetCore.Mvc;
using PersonalAI.Core.Services.Sessions;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/tool-invocations")]
public class ToolInvocationsController(ChatSessionsService sessions) : ControllerBase
{
    /// <summary>Resolves the approval and continues the turn in the background (same ownership model as
    /// SessionsController.SendMessage), streaming it to this response.</summary>
    [HttpPost("{id:guid}/resolve")]
    public async Task Resolve(Guid id, ResolveToolInvocationRequest request, CancellationToken ct)
    {
        await SseEventWriter.WriteTurnAsync(Response, await sessions.StartApprovalResolution(id, request.Approved, ct), ct);
    }
}
