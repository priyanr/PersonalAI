using PersonalAI.Api.Streaming;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.AI;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/models")]
public class ModelsController(AIProviderConfigService config, ILogger<ModelsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<List<AiModelDto>> GetModels([FromQuery] string providerKey = "ollama", CancellationToken ct = default)
    {
        return await config.GetModels(providerKey, ct);
    }

    [HttpGet("settings-fields")]
    public List<ModelSettingFieldDto> GetSettingsFields([FromQuery] string providerKey = "ollama")
    {
        return config.GetModelSettingFields(providerKey);
    }

    // Model names travel in the query string: they contain ':' and '/' (e.g. "llama3.1:8b", "org/model").
    [HttpPut("settings")]
    public async Task<IReadOnlyDictionary<string, string?>> UpdateSettings(
        [FromQuery] string providerKey, [FromQuery] string model, UpdateModelSettingsRequest request, CancellationToken ct)
    {
        return await config.UpdateModelSettings(providerKey, model, request, ct);
    }

    [HttpDelete("settings")]
    public async Task<IActionResult> ResetSettings([FromQuery] string providerKey, [FromQuery] string model, CancellationToken ct)
    {
        await config.ResetModelSettings(providerKey, model, ct);
        return NoContent();
    }

    [HttpPost("pull")]
    public async Task PullModel(PullModelRequest request, [FromQuery] string providerKey = "ollama", CancellationToken ct = default)
    {
        SseEventWriter.PrepareResponse(Response);
        // The response has already started streaming, so a failure is sent as an SSE error event, not a status code.
        try
        {
            await foreach (var progress in config.PullModel(providerKey, request.Name, ct))
                await SseEventWriter.WritePullProgressAsync(Response, progress, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // An unknown provider, or one that can't pull models, is an expected answer rather than a fault.
            if (ex is not (NotFoundException or NotSupportedException))
                logger.LogError(ex, "Error pulling model {ModelName} for provider {ProviderKey}", request.Name, providerKey);
            await SseEventWriter.WriteErrorAndCloseAsync(Response, ex.Message, ct);
        }
    }
}
