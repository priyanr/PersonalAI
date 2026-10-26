using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Settings;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/app-settings")]
public class AppSettingsController(AppSettingsService settings) : ControllerBase
{
    [HttpGet]
    public async Task<AppSettingsDto> Get(CancellationToken ct)
    {
        return await settings.Get(ct);
    }

    [HttpPatch]
    public async Task<AppSettingsDto> Update(UpdateAppSettingsRequest request, CancellationToken ct)
    {
        return await settings.Update(request, ct);
    }
}
