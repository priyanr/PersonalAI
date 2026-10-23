using System.Text.Json;
using PersonalAI.Api.Streaming;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Tools;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/skills")]
public class SkillsController(ISkillsRepository repo, ISkillExecutionService execution, ToolRegistry tools, ILogger<SkillsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SkillDto>>> GetAll(CancellationToken ct)
    {
        var skills = await repo.GetAllAsync(ct);
        return Ok(skills.Select(ToDto).ToList());
    }

    [HttpGet("available-tools")]
    public ActionResult<IReadOnlyList<AvailableToolDto>> GetAvailableTools() =>
        Ok(tools.ChatDefinitions.Select(d => new AvailableToolDto(d.Name, d.Description)).ToList());

    [HttpPost]
    public async Task<ActionResult<SkillDto>> Create(CreateSkillRequest request, CancellationToken ct)
    {
        var error = Validate(request.Name, request.Prompt, request.ModelName);
        if (error is not null) return BadRequest(new { message = error });

        var skill = await repo.CreateAsync(
            request.Name.Trim(), request.Description, request.Prompt, request.ProviderKey ?? "ollama",
            request.ModelName, request.NumCtx, request.EnabledTools ?? [], request.SecretKeys ?? [], ct);
        return CreatedAtAction(nameof(Get), new { id = skill.Id }, ToDto(skill));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SkillDto>> Get(Guid id, CancellationToken ct)
    {
        var skill = await repo.GetAsync(id, ct);
        return skill is null ? NotFound() : Ok(ToDto(skill));
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<SkillDto>> Update(Guid id, UpdateSkillRequest request, CancellationToken ct)
    {
        var error = Validate(request.Name, request.Prompt, request.ModelName);
        if (error is not null) return BadRequest(new { message = error });

        var skill = await repo.UpdateAsync(
            id, request.Name.Trim(), request.Description, request.Prompt, request.ProviderKey ?? "ollama",
            request.ModelName, request.NumCtx, request.EnabledTools ?? [], request.SecretKeys ?? [], ct);
        return skill is null ? NotFound() : Ok(ToDto(skill));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await repo.DeleteAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("{id:guid}/run")]
    public async Task Run(Guid id, RunSkillRequest request, CancellationToken ct)
    {
        SseEventWriter.PrepareResponse(Response);
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

    private static string? Validate(string? name, string? prompt, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(prompt)) return "Prompt is required.";
        if (string.IsNullOrWhiteSpace(modelName)) return "ModelName is required.";
        return null;
    }

    private static SkillDto ToDto(Skill s) => new(
        s.Id,
        s.Name,
        s.Description,
        s.Prompt,
        s.ProviderKey,
        s.ModelName,
        s.NumCtx,
        JsonSerializer.Deserialize<List<string>>(s.EnabledToolsJson) ?? [],
        JsonSerializer.Deserialize<List<string>>(s.SecretKeysJson) ?? [],
        s.CreatedAt,
        s.UpdatedAt);
}
