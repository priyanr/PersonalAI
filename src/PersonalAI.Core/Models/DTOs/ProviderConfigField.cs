namespace PersonalAI.Core.Models.DTOs;

public enum ProviderConfigFieldType
{
    Text,
    Secret,
    Url,
    Number,
    Boolean,
    Select
}

/// <summary>Declarative description of one piece of configuration an AI provider needs
/// (e.g. an API key or base URL) — the "ConfigurationAttributes Name, Type, Required" the UI
/// uses to render a per-provider settings form without hardcoding field lists.</summary>
/// <param name="Options">The allowed values of a <see cref="ProviderConfigFieldType.Select"/> field.</param>
/// <param name="Help">Optional explanatory text shown under the input.</param>
public record ProviderConfigField(
    string Name,
    string Label,
    ProviderConfigFieldType Type,
    bool Required,
    string? DefaultValue = null,
    IReadOnlyList<string>? Options = null,
    string? Help = null);
