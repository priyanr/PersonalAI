using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Secrets;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

[ApiController]
[Route("api/secrets")]
public class SecretsController(SecretsService secrets) : ControllerBase
{
    [HttpGet]
    public async Task<List<SecretDto>> GetAll(CancellationToken ct)
    {
        return await secrets.GetAll(ct);
    }

    [HttpPost]
    public async Task<SecretDto> Create(CreateSecretRequest request, CancellationToken ct)
    {
        return await secrets.Create(request, ct);
    }

    [HttpPut("{key}")]
    public async Task<SecretDto> Update(string key, UpdateSecretRequest request, CancellationToken ct)
    {
        return await secrets.Update(key, request, ct);
    }

    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        await secrets.Delete(key, ct);
        return NoContent();
    }
}
