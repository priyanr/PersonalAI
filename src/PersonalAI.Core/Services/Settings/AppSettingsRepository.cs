using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Settings;

public interface IAppSettingsRepository
{
    /// <summary>The tracked singleton settings row.</summary>
    Task<AppSettings> GetAsync(CancellationToken ct);
}

public class AppSettingsRepository(AppDbContext db) : IAppSettingsRepository
{
    public async Task<AppSettings> GetAsync(CancellationToken ct)
    {
        return await db.AppSettings.FindAsync([AppSettings.SingletonId], ct)
            ?? throw new InvalidOperationException("App settings row is missing — migration seed did not run.");
    }
}
