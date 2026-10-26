using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Skills;

public interface ISkillsRepository
{
    Task<List<Skill>> GetAllAsync(CancellationToken ct);

    Task<Skill?> GetAsync(Guid id, CancellationToken ct);
}

public class SkillsRepository(AppDbContext db) : ISkillsRepository
{
    public async Task<List<Skill>> GetAllAsync(CancellationToken ct)
    {
        // Ordered client-side, same rationale as ChatSessionRepository: SQLite can't translate
        // ORDER BY over DateTimeOffset, and skill counts for a local single-user app are tiny.
        var skills = await db.Skills.AsNoTracking().ToListAsync(ct);
        return skills.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public async Task<Skill?> GetAsync(Guid id, CancellationToken ct)
    {
        return await db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct);
    }
}
