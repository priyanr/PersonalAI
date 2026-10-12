using PersonalAI.Api.Streaming;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.AI;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/models")]
public class ModelsController(
    AIProviderRegistry providers,
    HybridCache cache,
    IModelConfigRepository modelConfigRepo,
    AIProviderConfigCache configCache,
    ILogger<ModelsController> logger) : ControllerBase
{
    private static readonly HybridCacheEntryOptions ModelListCacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromHours(1)
    };

    private static string ModelsCacheKey(string providerKey) => $"models:{providerKey.ToLowerInvariant()}";

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AiModelDto>>> GetModels([FromQuery] string providerKey = "ollama", CancellationToken ct = default)
    {
        if (!providers.TryResolve(providerKey, out var provider))
            return NotFound(new { message = $"Unknown AI provider '{providerKey}'." });

        try
        {
            var models = await cache.GetOrCreateAsync(
                ModelsCacheKey(providerKey),
                async token =>
                {
                    var list = await provider!.ListModelsAsync(token);
                    return list.Select(m => new AiModelDto(m.Name, m.Size, m.Digest, m.ModifiedAt, m.ParameterSize, m.QuantizationLevel)).ToList();
                },
                ModelListCacheOptions,
                cancellationToken: ct);
            // Settings are merged per request rather than cached with the list, so a saved change shows immediately.
            return Ok(models.Select(m => m with { Settings = configCache.GetModelSettings(provider!.Key, m.Name) }).ToList());
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Error listing models for provider {ProviderKey}", providerKey);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = $"Could not reach {provider!.DisplayName}: {ex.Message}" });
        }
    }

    [HttpGet("settings-fields")]
    public ActionResult<IReadOnlyList<ModelSettingFieldDto>> GetSettingsFields([FromQuery] string providerKey = "ollama")
    {
        if (!providers.TryResolve(providerKey, out var provider))
            return NotFound(new { message = $"Unknown AI provider '{providerKey}'." });

        return Ok(provider!.ModelSettingFields
            .Select(f => new ModelSettingFieldDto(f.Name, f.Label, f.Type.ToString(), f.DefaultValue, f.Options, f.Help))
            .ToList());
    }

    // Model names travel in the query string: they contain ':' and '/' (e.g. "llama3.1:8b", "org/model").
    [HttpPut("settings")]
    public async Task<ActionResult<IReadOnlyDictionary<string, string?>>> UpdateSettings(
        [FromQuery] string providerKey, [FromQuery] string model, UpdateModelSettingsRequest request, CancellationToken ct)
    {
        if (!providers.TryResolve(providerKey, out var provider))
            return NotFound(new { message = $"Unknown AI provider '{providerKey}'." });
        if (string.IsNullOrWhiteSpace(model))
            return BadRequest(new { message = "A model name is required." });

        var normalized = ModelSettings.Normalize(provider!.ModelSettingFields, request.Values, out var error);
        if (normalized is null)
            return BadRequest(new { message = error });

        await modelConfigRepo.ReplaceAsync(provider.Key, model, normalized, ct);
        configCache.SetModel(provider.Key, model, normalized);
        return Ok(configCache.GetModelSettings(provider.Key, model));
    }

    [HttpDelete("settings")]
    public async Task<IActionResult> ResetSettings([FromQuery] string providerKey, [FromQuery] string model, CancellationToken ct)
    {
        if (!providers.TryResolve(providerKey, out var provider))
            return NotFound(new { message = $"Unknown AI provider '{providerKey}'." });
        if (string.IsNullOrWhiteSpace(model))
            return BadRequest(new { message = "A model name is required." });

        var empty = new Dictionary<string, string?>();
        await modelConfigRepo.ReplaceAsync(provider!.Key, model, empty, ct);
        configCache.SetModel(provider.Key, model, empty);
        return NoContent();
    }

    [HttpPost("pull")]
    public async Task PullModel(PullModelRequest request, [FromQuery] string providerKey = "ollama", CancellationToken ct = default)
    {
        SseEventWriter.PrepareResponse(Response);

        if (!providers.TryResolve(providerKey, out var provider))
        {
            await SseEventWriter.WriteErrorAndCloseAsync(Response, $"Unknown AI provider '{providerKey}'.", ct);
            return;
        }

        try
        {
            await foreach (var progress in provider!.PullModelStreamAsync(request.Name, ct))
            {
                var dto = new PullProgressDto(progress.Status, progress.Digest, progress.Total, progress.Completed);
                await SseEventWriter.WritePullProgressAsync(Response, dto, ct);
            }
            await cache.RemoveAsync(ModelsCacheKey(providerKey), CancellationToken.None);
        }
        catch (NotSupportedException ex)
        {
            await SseEventWriter.WriteErrorAndCloseAsync(Response, ex.Message, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Error pulling model {ModelName} for provider {ProviderKey}", request.Name, providerKey);
            await SseEventWriter.WriteErrorAndCloseAsync(Response, ex.Message, ct);
        }
    }
}
