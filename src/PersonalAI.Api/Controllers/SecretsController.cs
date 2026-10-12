using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Secrets;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/secrets")]
public class SecretsController(ISecretsRepository repo, SecretsCache cache) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SecretDto>>> GetAll(CancellationToken ct)
    {
        var secrets = await repo.GetAllAsync(ct);
        return Ok(secrets.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<SecretDto>> Create(CreateSecretRequest request, CancellationToken ct)
    {
        var key = request.Key?.Trim() ?? "";
        if (key.Length == 0)
            return BadRequest(new { message = "Key is required." });
        if (string.IsNullOrEmpty(request.Value))
            return BadRequest(new { message = "Value is required." });

        try
        {
            var secret = await repo.CreateAsync(key, request.Value, ct);
            cache.Set(secret.Key, secret.Value);
            return Ok(ToDto(secret));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPut("{key}")]
    public async Task<ActionResult<SecretDto>> Update(string key, UpdateSecretRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Value))
            return BadRequest(new { message = "Value is required." });

        var updated = await repo.UpdateValueAsync(key, request.Value, ct);
        if (!updated)
            return NotFound(new { message = $"No secret with key '{key}'." });

        cache.Set(key, request.Value);
        var secrets = await repo.GetAllAsync(ct);
        return Ok(ToDto(secrets.Single(s => s.Key == key)));
    }

    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        var deleted = await repo.DeleteAsync(key, ct);
        if (!deleted)
            return NotFound(new { message = $"No secret with key '{key}'." });

        cache.Remove(key);
        return NoContent();
    }

    private static SecretDto ToDto(PersonalAI.Core.Models.Entities.Secret secret) =>
        new(secret.Key, !string.IsNullOrEmpty(secret.Value), secret.UpdatedAt);
}
