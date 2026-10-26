using PersonalAI.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Tools;

public interface IToolConfigRepository
{
    Task<Dictionary<string, string?>> GetValuesAsync(string toolName, CancellationToken ct);

    /// <summary>Every stored value, keyed by tool name, then field name.</summary>
    Task<Dictionary<string, Dictionary<string, string?>>> GetAllAsync(CancellationToken ct);
}

public class ToolConfigRepository(AppDbContext db) : IToolConfigRepository
{
    public async Task<Dictionary<string, string?>> GetValuesAsync(string toolName, CancellationToken ct)
    {
        return await db.ToolConfigs.AsNoTracking()
            .Where(c => c.ToolName == toolName)
            .ToDictionaryAsync(c => c.FieldName, c => c.Value, ct);
    }

    public async Task<Dictionary<string, Dictionary<string, string?>>> GetAllAsync(CancellationToken ct)
    {
        var all = await db.ToolConfigs.AsNoTracking().ToListAsync(ct);
        return all
            .GroupBy(c => c.ToolName)
            .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.FieldName, c => c.Value));
    }
}
