using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.AI;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/ai-providers")]
public class AIProvidersController(AIProviderRegistry providers, IAIProviderConfigRepository repo, AIProviderConfigCache cache) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AIProviderDto>>> GetAll(CancellationToken ct)
    {
        var result = new List<AIProviderDto>();
        foreach (var provider in providers.All)
        {
            var stored = await repo.GetValuesAsync(provider.Key, ct);
            result.Add(ToDto(provider.Key, provider.DisplayName, provider.ConfigFields, stored));
        }
        return Ok(result);
    }

    [HttpPatch("{key}")]
    public async Task<ActionResult<AIProviderDto>> Update(string key, UpdateAIProviderConfigRequest request, CancellationToken ct)
    {
        if (!providers.TryResolve(key, out var provider))
            return NotFound(new { message = $"Unknown AI provider '{key}'." });

        var fieldsByName = provider!.ConfigFields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var existing = await repo.GetValuesAsync(provider.Key, ct);

        var updatesToApply = new Dictionary<string, string?>();
        foreach (var (name, value) in request.Values)
        {
            if (!fieldsByName.TryGetValue(name, out var field))
                return BadRequest(new { message = $"Unknown config field '{name}' for provider '{provider.Key}'." });

            // Secret fields submitted blank mean "leave unchanged" — never null out a previously-set key by accident.
            if (field.Type == ProviderConfigFieldType.Secret && string.IsNullOrEmpty(value))
                continue;

            if (field.Type == ProviderConfigFieldType.Url && !string.IsNullOrEmpty(value) &&
                !Uri.TryCreate(value, UriKind.Absolute, out _))
                return BadRequest(new { message = $"'{name}' must be a valid absolute URL." });

            // Checked here rather than left to the provider, which would otherwise silently ignore a bad value
            // (e.g. "16k") and keep using the default the user was trying to change.
            if (field.Type == ProviderConfigFieldType.Number && !string.IsNullOrWhiteSpace(value) &&
                !(int.TryParse(value.Trim(), out var number) && number > 0))
                return BadRequest(new { message = $"'{name}' must be a positive whole number (or blank for the default)." });
            if (field.Name == AIProviderConfigCache.MaxOutputTokensField && int.TryParse(value?.Trim(), out var maxTokens) &&
                maxTokens > AIProviderConfigCache.MaxOutputTokensUpperBound)
                return BadRequest(new { message = $"'{name}' can be at most {AIProviderConfigCache.MaxOutputTokensUpperBound:N0}." });

            updatesToApply[name] = value;
        }

        var effective = new Dictionary<string, string?>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in updatesToApply)
            effective[name] = value;

        var missingRequired = provider.ConfigFields
            .Where(f => f.Required)
            .Where(f => string.IsNullOrEmpty(EffectiveValue(f, effective)))
            .Select(f => f.Name)
            .ToList();
        if (missingRequired.Count > 0)
            return BadRequest(new { message = "Missing required field(s).", fields = missingRequired });

        if (updatesToApply.Count > 0)
        {
            await repo.SetValuesAsync(provider.Key, updatesToApply, ct);
            existing = await repo.GetValuesAsync(provider.Key, ct);
        }
        cache.SetProvider(provider.Key, existing);

        return Ok(ToDto(provider.Key, provider.DisplayName, provider.ConfigFields, existing));
    }

    private static string? EffectiveValue(ProviderConfigField field, IReadOnlyDictionary<string, string?> values) =>
        values.TryGetValue(field.Name, out var v) && !string.IsNullOrEmpty(v) ? v : field.DefaultValue;

    private static AIProviderDto ToDto(string key, string displayName, IReadOnlyList<ProviderConfigField> fields, IReadOnlyDictionary<string, string?> stored)
    {
        var fieldDtos = fields.Select(f =>
        {
            var effective = EffectiveValue(f, stored);
            var hasValue = !string.IsNullOrEmpty(effective);
            return new ProviderConfigFieldDto(
                f.Name, f.Label, f.Type.ToString(), f.Required, hasValue,
                f.Type == ProviderConfigFieldType.Secret ? null : effective);
        }).ToList();

        var isConfigured = fields.Where(f => f.Required).All(f => !string.IsNullOrEmpty(EffectiveValue(f, stored)));
        return new AIProviderDto(key, displayName, fieldDtos, isConfigured);
    }
}
