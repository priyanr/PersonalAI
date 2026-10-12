using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Settings;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/app-settings")]
public class AppSettingsController(IAppSettingsRepository repo, AppSettingsCache cache) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AppSettingsDto>> Get(CancellationToken ct)
    {
        var settings = await repo.GetAsync(ct);
        return Ok(new AppSettingsDto(
            settings.WebSearchGuidance ?? AppSettingsCache.DefaultWebSearchGuidance,
            settings.WebSearchGuidance is null,
            settings.ThinkingEnabled));
    }

    [HttpPatch]
    public async Task<ActionResult<AppSettingsDto>> Update(UpdateAppSettingsRequest request, CancellationToken ct)
    {
        var settings = await repo.UpdateAsync(request.WebSearchGuidance, request.ThinkingEnabled, ct);
        cache.Set(settings.WebSearchGuidance, settings.ThinkingEnabled);

        return Ok(new AppSettingsDto(
            settings.WebSearchGuidance ?? AppSettingsCache.DefaultWebSearchGuidance,
            settings.WebSearchGuidance is null,
            settings.ThinkingEnabled));
    }
}
