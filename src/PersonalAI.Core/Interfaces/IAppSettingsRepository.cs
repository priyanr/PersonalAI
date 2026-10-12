using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Interfaces;

public interface IAppSettingsRepository
{
    Task<AppSettings> GetAsync(CancellationToken ct);
    Task<AppSettings> UpdateAsync(string? webSearchGuidance, bool thinkingEnabled, CancellationToken ct);
}
