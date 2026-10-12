using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Core.Tools;
using Microsoft.AspNetCore.Mvc;

namespace PersonalAI.Api.Controllers;

/// <summary>Generic per-tool settings, mirroring AIProvidersController for AI providers. Only tools that
/// declare SettingsFields show up here — the 8 tools with no configuration are omitted entirely. Hidden
/// fields a tool manages itself (e.g. Gmail's OAuth tokens) are never exposed here since they're
/// deliberately absent from SettingsFields.</summary>
[ApiController]
[Route("api/tools")]
public class ToolsController(ToolRegistry tools, IToolConfigRepository repo, ToolConfigCache cache) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ToolSettingsDto>>> GetAll(CancellationToken ct)
    {
        var result = new List<ToolSettingsDto>();
        foreach (var tool in tools.All)
        {
            if (tool.SettingsFields.Count == 0) continue;

            var stored = await repo.GetValuesAsync(tool.Name, ct);
            result.Add(ToDto(tool, stored));
        }
        return Ok(result);
    }

    [HttpPatch("{name}")]
    public async Task<ActionResult<ToolSettingsDto>> Update(string name, UpdateToolConfigRequest request, CancellationToken ct)
    {
        if (!tools.TryGet(name, out var tool) || tool.SettingsFields.Count == 0)
            return NotFound(new { message = $"Unknown tool '{name}' or it has no configurable settings." });

        var fieldsByName = tool.SettingsFields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var existing = await repo.GetValuesAsync(tool.Name, ct);

        var updatesToApply = new Dictionary<string, string?>();
        foreach (var (fieldName, value) in request.Values)
        {
            if (!fieldsByName.TryGetValue(fieldName, out var field))
                return BadRequest(new { message = $"Unknown config field '{fieldName}' for tool '{tool.Name}'." });

            if (field.Type == ToolSettingFieldType.Custom)
                return BadRequest(new { message = $"'{fieldName}' is a custom field and cannot be set via this endpoint." });

            // Secret fields submitted blank mean "leave unchanged" — never null out a previously-set value by accident.
            if (field.Type == ToolSettingFieldType.Secret && string.IsNullOrEmpty(value))
                continue;

            if (field.Type == ToolSettingFieldType.Url && !string.IsNullOrEmpty(value) &&
                !Uri.TryCreate(value, UriKind.Absolute, out _))
                return BadRequest(new { message = $"'{fieldName}' must be a valid absolute URL." });

            if (field.Type == ToolSettingFieldType.Number && !string.IsNullOrEmpty(value) &&
                !(int.TryParse(value, out var number) && number > 0))
                return BadRequest(new { message = $"'{fieldName}' must be a positive whole number." });

            updatesToApply[fieldName] = value;
        }

        var effective = new Dictionary<string, string?>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldName, value) in updatesToApply)
            effective[fieldName] = value;

        var missingRequired = tool.SettingsFields
            .Where(f => f.Required && f.Type != ToolSettingFieldType.Custom)
            .Where(f => string.IsNullOrEmpty(EffectiveValue(f, effective)))
            .Select(f => f.Name)
            .ToList();
        if (missingRequired.Count > 0)
            return BadRequest(new { message = "Missing required field(s).", fields = missingRequired });

        if (updatesToApply.Count > 0)
        {
            await repo.SetValuesAsync(tool.Name, updatesToApply, ct);
            existing = await repo.GetValuesAsync(tool.Name, ct);
        }
        cache.SetTool(tool.Name, existing);

        return Ok(ToDto(tool, existing));
    }

    private static string? EffectiveValue(ToolSettingField field, IReadOnlyDictionary<string, string?> values) =>
        values.TryGetValue(field.Name, out var v) && !string.IsNullOrEmpty(v) ? v : field.DefaultValue;

    private ToolSettingsDto ToDto(ITool tool, IReadOnlyDictionary<string, string?> stored)
    {
        var fieldDtos = tool.SettingsFields.Select(f =>
        {
            if (f.Type == ToolSettingFieldType.Custom)
                return new ToolSettingFieldDto(f.Name, f.Label, f.Type.ToString(), f.Required, false, null, f.CustomComponentKey);

            var effective = EffectiveValue(f, stored);
            var hasValue = !string.IsNullOrEmpty(effective);
            return new ToolSettingFieldDto(
                f.Name, f.Label, f.Type.ToString(), f.Required, hasValue,
                f.Type == ToolSettingFieldType.Secret ? null : effective,
                f.CustomComponentKey, f.Placeholder, f.Help);
        }).ToList();

        var isConfigured = tool.SettingsFields
            .Where(f => f.Required && f.Type != ToolSettingFieldType.Custom)
            .All(f => !string.IsNullOrEmpty(EffectiveValue(f, stored)));

        return new ToolSettingsDto(tool.Name, DisplayName(tool.Name), fieldDtos, isConfigured, tool.IsConnected(cache));
    }

    private static string DisplayName(string toolName) => toolName switch
    {
        "gmail" => "Gmail",
        _ => string.Join(' ', toolName.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]))
    };
}
