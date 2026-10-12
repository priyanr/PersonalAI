using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Settings;

public class AppSettingsRepository(AppDbContext db) : IAppSettingsRepository
{
    public async Task<AppSettings> GetAsync(CancellationToken ct) =>
        await db.AppSettings.FindAsync([AppSettings.SingletonId], ct)
            ?? throw new InvalidOperationException("App settings row is missing — migration seed did not run.");

    public async Task<AppSettings> UpdateAsync(string? webSearchGuidance, bool thinkingEnabled, CancellationToken ct)
    {
        var settings = await GetAsync(ct);
        settings.WebSearchGuidance = webSearchGuidance;
        settings.ThinkingEnabled = thinkingEnabled;
        await db.SaveChangesAsync(ct);
        return settings;
    }
}
