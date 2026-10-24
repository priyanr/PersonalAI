using PersonalAI.Api.Streaming;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Tools;
using Microsoft.AspNetCore.Mvc;
using PersonalAI.Core.Services.Skills;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/skills")]
public class SkillsController(SkillsService skills, SkillExecutionService execution, ToolRegistry tools, ILogger<SkillsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<List<SkillDto>> GetAll(CancellationToken ct)
    {
        return await skills.GetAll(ct);
    }

    [HttpGet("available-tools")]
    public List<AvailableToolDto> GetAvailableTools()
    {
        return tools.ChatDefinitions.Select(d => new AvailableToolDto(d.Name, d.Description)).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<SkillDto>> Create(SaveSkillRequest request, CancellationToken ct)
    {
        var skill = await skills.Create(request, ct);
        return CreatedAtAction(nameof(Get), new { id = skill.Id }, skill);
    }

    [HttpGet("{id:guid}")]
    public async Task<SkillDto> Get(Guid id, CancellationToken ct)
    {
        return await skills.Get(id, ct);
    }

    [HttpPatch("{id:guid}")]
    public async Task<SkillDto> Update(Guid id, SaveSkillRequest request, CancellationToken ct)
    {
        return await skills.Update(id, request, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await skills.Delete(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/run")]
    public async Task Run(Guid id, RunSkillRequest request, CancellationToken ct)
    {
        SseEventWriter.PrepareResponse(Response);
        // The response has already started streaming, so a failure is sent as an SSE error event, not a status code.
        try
        {
            await foreach (var evt in execution.RunAsync(id, request.Input, ct))
                await SseEventWriter.WriteAsync(Response, evt, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Error while running skill {SkillId}", id);
            await SseEventWriter.WriteErrorAndCloseAsync(Response, ex.Message, ct);
        }
    }
}
