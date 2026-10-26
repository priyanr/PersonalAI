using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Services.Settings;

/// <summary>App-wide settings (/api/app-settings). Every save also refreshes AppSettingsCache for the tools that read it.</summary>
public class AppSettingsService(AppDbContext db, IAppSettingsRepository repo, AppSettingsCache cache)
{
    public async Task<AppSettingsDto> Get(CancellationToken ct)
    {
        return ToDto(await repo.GetAsync(ct));
    }

    public async Task<AppSettingsDto> Update(UpdateAppSettingsRequest request, CancellationToken ct)
    {
        var settings = await repo.GetAsync(ct);
        settings.WebSearchGuidance = request.WebSearchGuidance;
        settings.ThinkingEnabled = request.ThinkingEnabled;
        await db.SaveChangesAsync(ct);
        cache.Set(settings.WebSearchGuidance, settings.ThinkingEnabled);
        return ToDto(settings);
    }

    private static AppSettingsDto ToDto(AppSettings settings)
    {
        return new AppSettingsDto(
            settings.WebSearchGuidance ?? AppSettingsCache.DefaultWebSearchGuidance,
            settings.WebSearchGuidance is null,
            settings.ThinkingEnabled);
    }
}
