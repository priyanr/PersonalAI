using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Tools;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/tools")]
public class ToolsController(ToolConfigService toolConfig) : ControllerBase
{
    [HttpGet]
    public async Task<List<ToolSettingsDto>> GetAll(CancellationToken ct)
    {
        return await toolConfig.GetAll(ct);
    }

    [HttpPatch("{name}")]
    public async Task<ToolSettingsDto> Update(string name, UpdateToolConfigRequest request, CancellationToken ct)
    {
        return await toolConfig.Update(name, request, ct);
    }
}
