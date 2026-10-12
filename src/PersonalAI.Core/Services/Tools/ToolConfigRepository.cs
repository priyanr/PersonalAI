using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Tools;

public class ToolConfigRepository(AppDbContext db) : IToolConfigRepository
{
    public async Task<IReadOnlyDictionary<string, string?>> GetValuesAsync(string toolName, CancellationToken ct) =>
        await db.ToolConfigs
            .Where(c => c.ToolName == toolName)
            .ToDictionaryAsync(c => c.FieldName, c => c.Value, ct);

    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>> GetAllAsync(CancellationToken ct)
    {
        var all = await db.ToolConfigs.ToListAsync(ct);
        return all
            .GroupBy(c => c.ToolName)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, string?>)g.ToDictionary(c => c.FieldName, c => c.Value));
    }

    public async Task SetValuesAsync(string toolName, IReadOnlyDictionary<string, string?> updates, CancellationToken ct)
    {
        var existing = await db.ToolConfigs
            .Where(c => c.ToolName == toolName)
            .ToDictionaryAsync(c => c.FieldName, ct);

        foreach (var (fieldName, value) in updates)
        {
            if (existing.TryGetValue(fieldName, out var row))
            {
                row.Value = value;
            }
            else
            {
                db.ToolConfigs.Add(new ToolConfig { ToolName = toolName, FieldName = fieldName, Value = value });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
