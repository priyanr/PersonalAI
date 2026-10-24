using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Tools;
using Microsoft.EntityFrameworkCore;

namespace PersonalAI.Core.Services.Tools;

/// <summary>Per-tool settings (/api/tools), plus the one write path for ToolConfig rows: every save also updates
/// ToolConfigCache, which the singleton tools read. Only tools that declare SettingsFields are listed; hidden fields a
/// tool manages itself (e.g. Gmail's OAuth tokens) are never exposed since they're absent from SettingsFields.</summary>
public class ToolConfigService(AppDbContext db, IToolConfigRepository repo, ToolRegistry tools, ToolConfigCache cache)
{
    public async Task<List<ToolSettingsDto>> GetAll(CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        return tools.All
            .Where(t => t.SettingsFields.Count > 0)
            .Select(t => ToDto(t, all.GetValueOrDefault(t.Name) ?? new Dictionary<string, string?>()))
            .ToList();
    }

    public async Task<ToolSettingsDto> Update(string name, UpdateToolConfigRequest request, CancellationToken ct)
    {
        if (!tools.TryGet(name, out var tool) || tool.SettingsFields.Count == 0)
            throw new NotFoundException($"Unknown tool '{name}' or it has no configurable settings.");

        var fieldsByName = tool.SettingsFields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var updates = new Dictionary<string, string?>();
        foreach (var (fieldName, value) in request.Values)
        {
            if (!fieldsByName.TryGetValue(fieldName, out var field))
                throw new InvalidRequestException($"Unknown config field '{fieldName}' for tool '{tool.Name}'.");
            if (field.Type == ToolSettingFieldType.Custom)
                throw new InvalidRequestException($"'{fieldName}' is a custom field and cannot be set via this endpoint.");

            // Secret fields submitted blank mean "leave unchanged" — never null out a previously-set value by accident.
            if (field.Type == ToolSettingFieldType.Secret && string.IsNullOrEmpty(value))
                continue;

            if (field.Type == ToolSettingFieldType.Url && !string.IsNullOrEmpty(value) && !Uri.TryCreate(value, UriKind.Absolute, out _))
                throw new InvalidRequestException($"'{fieldName}' must be a valid absolute URL.");
            if (field.Type == ToolSettingFieldType.Number && !string.IsNullOrEmpty(value) && !(int.TryParse(value, out var number) && number > 0))
                throw new InvalidRequestException($"'{fieldName}' must be a positive whole number.");

            // Keyed by the field's own spelling: the startup load into ToolConfigCache is case-insensitive, so a
            // second row differing only in case would stop the API from starting.
            updates[field.Name] = value;
        }

        var effective = new Dictionary<string, string?>(await repo.GetValuesAsync(tool.Name, ct), StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldName, value) in updates)
            effective[fieldName] = value;

        var missingRequired = tool.SettingsFields
            .Where(f => f.Required && f.Type != ToolSettingFieldType.Custom && string.IsNullOrEmpty(EffectiveValue(f, effective)))
            .Select(f => f.Name)
            .ToList();
        if (missingRequired.Count > 0)
            throw new InvalidRequestException($"Missing required field(s): {string.Join(", ", missingRequired)}.");

        await SaveValues(tool.Name, updates, ct);
        return ToDto(tool, effective);
    }

    /// <summary>Upserts only the field names present in <paramref name="updates"/> (a null value clears that field) and
    /// merges them into ToolConfigCache.</summary>
    public async Task SaveValues(string toolName, IReadOnlyDictionary<string, string?> updates, CancellationToken ct)
    {
        if (updates.Count == 0) return;

        var existing = await db.ToolConfigs.Where(c => c.ToolName == toolName).ToDictionaryAsync(c => c.FieldName, ct);
        foreach (var (fieldName, value) in updates)
        {
            if (existing.TryGetValue(fieldName, out var row))
                row.Value = value;
            else
                db.ToolConfigs.Add(new ToolConfig { ToolName = toolName, FieldName = fieldName, Value = value });
        }
        await db.SaveChangesAsync(ct);
        cache.MergeTool(toolName, updates);
    }

    private static string? EffectiveValue(ToolSettingField field, IReadOnlyDictionary<string, string?> values)
    {
        return values.TryGetValue(field.Name, out var v) && !string.IsNullOrEmpty(v) ? v : field.DefaultValue;
    }

    private ToolSettingsDto ToDto(ITool tool, IReadOnlyDictionary<string, string?> stored)
    {
        var fieldDtos = tool.SettingsFields.Select(f =>
        {
            if (f.Type == ToolSettingFieldType.Custom)
                return new ToolSettingFieldDto(f.Name, f.Label, f.Type.ToString(), f.Required, false, null, f.CustomComponentKey);

            var effective = EffectiveValue(f, stored);
            return new ToolSettingFieldDto(
                f.Name, f.Label, f.Type.ToString(), f.Required, !string.IsNullOrEmpty(effective),
                f.Type == ToolSettingFieldType.Secret ? null : effective,
                f.CustomComponentKey, f.Placeholder, f.Help);
        }).ToList();

        var isConfigured = tool.SettingsFields
            .Where(f => f.Required && f.Type != ToolSettingFieldType.Custom)
            .All(f => !string.IsNullOrEmpty(EffectiveValue(f, stored)));

        return new ToolSettingsDto(tool.Name, DisplayName(tool.Name), fieldDtos, isConfigured, tool.IsConnected(cache));
    }

    private static string DisplayName(string toolName)
    {
        return toolName switch
        {
            "gmail" => "Gmail",
            "ssh" => "SSH",
            _ => string.Join(' ', toolName.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]))
        };
    }
}
