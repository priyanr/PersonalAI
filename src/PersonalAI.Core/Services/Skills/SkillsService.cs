using System.Text.Json;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Services.Skills;

/// <summary>Create, edit and delete Skills (/api/skills). Running one is SkillExecutionService.</summary>
public class SkillsService(AppDbContext db, ISkillsRepository skills)
{
    public async Task<List<SkillDto>> GetAll(CancellationToken ct)
    {
        return (await skills.GetAllAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<SkillDto> Get(Guid id, CancellationToken ct)
    {
        return ToDto(await skills.GetAsync(id, ct) ?? throw NotFound(id));
    }

    public async Task<SkillDto> Create(SaveSkillRequest request, CancellationToken ct)
    {
        Validate(request);
        var skill = new Skill { Name = request.Name, Prompt = request.Prompt, ModelName = request.ModelName };
        Apply(skill, request);
        db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        return ToDto(skill);
    }

    public async Task<SkillDto> Update(Guid id, SaveSkillRequest request, CancellationToken ct)
    {
        Validate(request);
        var skill = await skills.GetAsync(id, ct) ?? throw NotFound(id);
        Apply(skill, request);
        skill.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(skill);
    }

    public async Task Delete(Guid id, CancellationToken ct)
    {
        var skill = await skills.GetAsync(id, ct) ?? throw NotFound(id);
        db.Skills.Remove(skill);
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(SaveSkillRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidRequestException("Name is required.");
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new InvalidRequestException("Prompt is required.");
        if (string.IsNullOrWhiteSpace(request.ModelName)) throw new InvalidRequestException("ModelName is required.");
    }

    private static void Apply(Skill skill, SaveSkillRequest request)
    {
        skill.Name = request.Name.Trim();
        skill.Description = request.Description;
        skill.Prompt = request.Prompt;
        skill.ProviderKey = request.ProviderKey ?? "ollama";
        skill.ModelName = request.ModelName;
        skill.NumCtx = request.NumCtx;
        skill.EnabledToolsJson = JsonSerializer.Serialize(request.EnabledTools ?? []);
        skill.SecretKeysJson = JsonSerializer.Serialize(request.SecretKeys ?? []);
    }

    private static NotFoundException NotFound(Guid id)
    {
        return new NotFoundException($"Skill {id} not found.");
    }

    private static SkillDto ToDto(Skill s)
    {
        return new SkillDto(
            s.Id, s.Name, s.Description, s.Prompt, s.ProviderKey, s.ModelName, s.NumCtx,
            JsonSerializer.Deserialize<List<string>>(s.EnabledToolsJson) ?? [],
            JsonSerializer.Deserialize<List<string>>(s.SecretKeysJson) ?? [],
            s.CreatedAt, s.UpdatedAt);
    }
}
