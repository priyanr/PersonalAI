using PersonalAI.Api.Streaming;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities.Enums;
using Microsoft.AspNetCore.Mvc;
using PersonalAI.Core.Services.Agents;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController(AgentsService agents, AgentExecutionService execution, ILogger<AgentsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<List<AgentDto>> GetAll(CancellationToken ct)
    {
        return await agents.GetAll(ct);
    }

    [HttpGet("types")]
    public List<AgentTypeDto> GetTypes()
    {
        return agents.GetTypes();
    }

    [HttpPost]
    public async Task<ActionResult<AgentDto>> Create(SaveAgentRequest request, CancellationToken ct)
    {
        var agent = await agents.Create(request, ct);
        return CreatedAtAction(nameof(Get), new { id = agent.Id }, agent);
    }

    [HttpGet("{id:guid}")]
    public async Task<AgentDto> Get(Guid id, CancellationToken ct)
    {
        return await agents.Get(id, ct);
    }

    [HttpPatch("{id:guid}")]
    public async Task<AgentDto> Update(Guid id, SaveAgentRequest request, CancellationToken ct)
    {
        return await agents.Update(id, request, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await agents.Delete(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/run")]
    public async Task Run(Guid id, RunAgentRequest request, CancellationToken ct)
    {
        SseEventWriter.PrepareResponse(Response);
        // The response has already started streaming, so a failure is sent as an SSE error event, not a status code.
        try
        {
            await foreach (var evt in execution.RunAsync(id, AgentRunTrigger.Manual, ct))
                await SseEventWriter.WriteAsync(Response, evt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Error while running agent {AgentId}", id);
            await SseEventWriter.WriteErrorAndCloseAsync(Response, ex.Message, ct);
        }
    }

    /// <summary>Chat on a finished run: streams the agent's reply to a follow-up message, which is appended to the run.</summary>
    [HttpPost("{id:guid}/runs/{runId:guid}/messages")]
    public async Task SendRunMessage(Guid id, Guid runId, RunMessageRequest request, CancellationToken ct)
    {
        await agents.EnsureRunAcceptsMessage(id, runId, request.Message, ct);

        SseEventWriter.PrepareResponse(Response);
        try
        {
            await foreach (var evt in execution.ContinueAsync(id, runId, request.Message!, ct))
                await SseEventWriter.WriteAsync(Response, evt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Error while continuing run {RunId} of agent {AgentId}", runId, id);
            await SseEventWriter.WriteErrorAndCloseAsync(Response, ex.Message, ct);
        }
    }

    [HttpGet("{id:guid}/runs")]
    public async Task<List<AgentRunSummaryDto>> GetRuns(Guid id, [FromQuery] int limit, CancellationToken ct)
    {
        return await agents.GetRuns(id, limit, ct);
    }

    [HttpGet("{id:guid}/runs/{runId:guid}")]
    public async Task<AgentRunDetailDto> GetRun(Guid id, Guid runId, CancellationToken ct)
    {
        return await agents.GetRun(id, runId, ct);
    }
}
