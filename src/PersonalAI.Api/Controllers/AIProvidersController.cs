using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.AI;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/ai-providers")]
public class AIProvidersController(AIProviderConfigService config) : ControllerBase
{
    [HttpGet]
    public async Task<List<AIProviderDto>> GetAll(CancellationToken ct)
    {
        return await config.GetProviders(ct);
    }

    [HttpPatch("{key}")]
    public async Task<AIProviderDto> Update(string key, UpdateAIProviderConfigRequest request, CancellationToken ct)
    {
        return await config.UpdateProvider(key, request, ct);
    }
}
