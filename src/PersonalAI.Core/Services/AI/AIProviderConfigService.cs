using System.Runtime.CompilerServices;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace PersonalAI.Core.Services.AI;

/// <summary>Provider connection settings (/api/ai-providers) and the models each provider offers with their per-model
/// settings (/api/models). Every save also updates AIProviderConfigCache, which the providers read on each call.</summary>
public class AIProviderConfigService(
    AppDbContext db, IAIProviderConfigRepository repo, AIProviderRegistry providers, AIProviderConfigCache cache, HybridCache modelLists)
{
    private static readonly HybridCacheEntryOptions ModelListCacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromHours(1)
    };

    public async Task<List<AIProviderDto>> GetProviders(CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        return providers.All
            .Select(p => ToDto(p, all.GetValueOrDefault(p.Key) ?? new Dictionary<string, string?>()))
            .ToList();
    }

    public async Task<AIProviderDto> UpdateProvider(string key, UpdateAIProviderConfigRequest request, CancellationToken ct)
    {
        var provider = Resolve(key);
        var fieldsByName = provider.ConfigFields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        var updates = new Dictionary<string, string?>();
        foreach (var (name, value) in request.Values)
        {
            if (!fieldsByName.TryGetValue(name, out var field))
                throw new InvalidRequestException($"Unknown config field '{name}' for provider '{provider.Key}'.");

            // Secret fields submitted blank mean "leave unchanged" — never null out a previously-set key by accident.
            if (field.Type == ProviderConfigFieldType.Secret && string.IsNullOrEmpty(value))
                continue;

            if (field.Type == ProviderConfigFieldType.Url && !string.IsNullOrEmpty(value) && !Uri.TryCreate(value, UriKind.Absolute, out _))
                throw new InvalidRequestException($"'{name}' must be a valid absolute URL.");

            // Checked here rather than left to the provider, which would otherwise silently ignore a bad value
            // (e.g. "16k") and keep using the default the user was trying to change.
            if (field.Type == ProviderConfigFieldType.Number && !string.IsNullOrWhiteSpace(value) &&
                !(int.TryParse(value.Trim(), out var number) && number > 0))
                throw new InvalidRequestException($"'{name}' must be a positive whole number (or blank for the default).");
            if (field.Name == AIProviderConfigCache.MaxOutputTokensField && int.TryParse(value?.Trim(), out var maxTokens) &&
                maxTokens > AIProviderConfigCache.MaxOutputTokensUpperBound)
                throw new InvalidRequestException($"'{name}' can be at most {AIProviderConfigCache.MaxOutputTokensUpperBound:N0}.");

            updates[name] = value;
        }

        var existing = await db.AIProviderConfigs.Where(c => c.ProviderKey == provider.Key).ToDictionaryAsync(c => c.FieldName, ct);
        var effective = existing.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in updates)
            effective[name] = value;

        var missingRequired = provider.ConfigFields
            .Where(f => f.Required && string.IsNullOrEmpty(EffectiveValue(f, effective)))
            .Select(f => f.Name)
            .ToList();
        if (missingRequired.Count > 0)
            throw new InvalidRequestException($"Missing required field(s): {string.Join(", ", missingRequired)}.");

        foreach (var (name, value) in updates)
        {
            if (existing.TryGetValue(name, out var row))
                row.Value = value;
            else
                db.AIProviderConfigs.Add(new AIProviderConfig { ProviderKey = provider.Key, FieldName = name, Value = value });
        }
        await db.SaveChangesAsync(ct);
        cache.SetProvider(provider.Key, effective);

        return ToDto(provider, effective);
    }

    /// <summary>The provider's models, listed from the provider at most once an hour, with each model's saved settings.</summary>
    public async Task<List<AiModelDto>> GetModels(string providerKey, CancellationToken ct)
    {
        var provider = Resolve(providerKey);
        List<AiModelDto> models;
        try
        {
            models = await modelLists.GetOrCreateAsync(
                ModelListKey(providerKey),
                async token =>
                {
                    var list = await provider.ListModelsAsync(token);
                    return list.Select(m => new AiModelDto(m.Name, m.Size, m.Digest, m.ModifiedAt, m.ParameterSize, m.QuantizationLevel)).ToList();
                },
                ModelListCacheOptions,
                cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            throw new UpstreamUnavailableException($"Could not reach {provider.DisplayName}: {ex.Message}", ex);
        }
        // Settings are merged per request rather than cached with the list, so a saved change shows immediately.
        return models.Select(m => m with { Settings = cache.GetModelSettings(provider.Key, m.Name) }).ToList();
    }

    public List<ModelSettingFieldDto> GetModelSettingFields(string providerKey)
    {
        return Resolve(providerKey).ModelSettingFields
            .Select(f => new ModelSettingFieldDto(f.Name, f.Label, f.Type.ToString(), f.DefaultValue, f.Options, f.Help))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, string?>> UpdateModelSettings(
        string providerKey, string model, UpdateModelSettingsRequest request, CancellationToken ct)
    {
        var provider = Resolve(providerKey);
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidRequestException("A model name is required.");

        var normalized = ModelSettings.Normalize(provider.ModelSettingFields, request.Values, out var error)
            ?? throw new InvalidRequestException(error!);
        await ReplaceModelSettings(provider.Key, model, normalized, ct);
        return cache.GetModelSettings(provider.Key, model);
    }

    public async Task ResetModelSettings(string providerKey, string model, CancellationToken ct)
    {
        var provider = Resolve(providerKey);
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidRequestException("A model name is required.");

        await ReplaceModelSettings(provider.Key, model, new Dictionary<string, string?>(), ct);
    }

    /// <summary>Streams a model download; the provider's cached model list is dropped once it finishes.</summary>
    public async IAsyncEnumerable<PullProgressDto> PullModel(string providerKey, string modelName, [EnumeratorCancellation] CancellationToken ct)
    {
        var provider = Resolve(providerKey);
        await foreach (var progress in provider.PullModelStreamAsync(modelName, ct))
            yield return new PullProgressDto(progress.Status, progress.Digest, progress.Total, progress.Completed);
        await modelLists.RemoveAsync(ModelListKey(providerKey), CancellationToken.None);
    }

    /// <summary>Replaces all of one model's settings with <paramref name="values"/>; blank values are dropped.</summary>
    private async Task ReplaceModelSettings(string providerKey, string modelName, IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        db.ModelConfigs.RemoveRange(await db.ModelConfigs.Where(c => c.ProviderKey == providerKey && c.ModelName == modelName).ToListAsync(ct));
        foreach (var (fieldName, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            db.ModelConfigs.Add(new ModelConfig { ProviderKey = providerKey, ModelName = modelName, FieldName = fieldName, Value = value });
        }
        await db.SaveChangesAsync(ct);
        cache.SetModel(providerKey, modelName, values);
    }

    private IAIProvider Resolve(string providerKey)
    {
        return providers.TryResolve(providerKey, out var provider)
            ? provider!
            : throw new NotFoundException($"Unknown AI provider '{providerKey}'.");
    }

    private static string ModelListKey(string providerKey)
    {
        return $"models:{providerKey.ToLowerInvariant()}";
    }

    private static string? EffectiveValue(ProviderConfigField field, IReadOnlyDictionary<string, string?> values)
    {
        return values.TryGetValue(field.Name, out var v) && !string.IsNullOrEmpty(v) ? v : field.DefaultValue;
    }

    private static AIProviderDto ToDto(IAIProvider provider, IReadOnlyDictionary<string, string?> stored)
    {
        var fieldDtos = provider.ConfigFields.Select(f =>
        {
            var effective = EffectiveValue(f, stored);
            return new ProviderConfigFieldDto(
                f.Name, f.Label, f.Type.ToString(), f.Required, !string.IsNullOrEmpty(effective),
                f.Type == ProviderConfigFieldType.Secret ? null : effective);
        }).ToList();

        var isConfigured = provider.ConfigFields.Where(f => f.Required).All(f => !string.IsNullOrEmpty(EffectiveValue(f, stored)));
        return new AIProviderDto(provider.Key, provider.DisplayName, fieldDtos, isConfigured);
    }
}
