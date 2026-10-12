using System.Text.Json;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Skills;

public class SkillsRepository(AppDbContext db) : ISkillsRepository
{
    public async Task<IReadOnlyList<Skill>> GetAllAsync(CancellationToken ct)
    {
        // Ordered client-side, same rationale as ChatSessionRepository: SQLite can't translate
        // ORDER BY over DateTimeOffset, and skill counts for a local single-user app are tiny.
        var skills = await db.Skills.ToListAsync(ct);
        return skills.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public async Task<Skill?> GetAsync(Guid id, CancellationToken ct) =>
        await db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<Skill> CreateAsync(
        string name, string? description, string prompt, string providerKey, string modelName, int? numCtx,
        IReadOnlyList<string> enabledTools, IReadOnlyList<string> secretKeys, CancellationToken ct)
    {
        var skill = new Skill
        {
            Name = name,
            Description = description,
            Prompt = prompt,
            ProviderKey = providerKey,
            ModelName = modelName,
            NumCtx = numCtx,
            EnabledToolsJson = JsonSerializer.Serialize(enabledTools),
            SecretKeysJson = JsonSerializer.Serialize(secretKeys)
        };
        db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        return skill;
    }

    public async Task<Skill?> UpdateAsync(
        Guid id, string name, string? description, string prompt, string providerKey, string modelName, int? numCtx,
        IReadOnlyList<string> enabledTools, IReadOnlyList<string> secretKeys, CancellationToken ct)
    {
        var skill = await db.Skills.FindAsync([id], ct);
        if (skill is null) return null;

        skill.Name = name;
        skill.Description = description;
        skill.Prompt = prompt;
        skill.ProviderKey = providerKey;
        skill.ModelName = modelName;
        skill.NumCtx = numCtx;
        skill.EnabledToolsJson = JsonSerializer.Serialize(enabledTools);
        skill.SecretKeysJson = JsonSerializer.Serialize(secretKeys);
        skill.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return skill;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var skill = await db.Skills.FindAsync([id], ct);
        if (skill is null) return false;

        db.Skills.Remove(skill);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
