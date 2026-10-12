using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Interfaces;

public interface ISkillsRepository
{
    Task<IReadOnlyList<Skill>> GetAllAsync(CancellationToken ct);

    Task<Skill?> GetAsync(Guid id, CancellationToken ct);

    Task<Skill> CreateAsync(
        string name, string? description, string prompt, string providerKey, string modelName, int? numCtx,
        IReadOnlyList<string> enabledTools, IReadOnlyList<string> secretKeys, CancellationToken ct);

    /// <summary>Returns null if no skill has that id.</summary>
    Task<Skill?> UpdateAsync(
        Guid id, string name, string? description, string prompt, string providerKey, string modelName, int? numCtx,
        IReadOnlyList<string> enabledTools, IReadOnlyList<string> secretKeys, CancellationToken ct);

    /// <summary>Returns false if no skill has that id.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
