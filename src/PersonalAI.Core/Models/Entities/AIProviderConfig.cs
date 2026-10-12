namespace PersonalAI.Core.Models.Entities;

/// <summary>One configured field for one AI provider (e.g. ProviderKey="openai", FieldName="ApiKey").
/// A lightweight, provider-scoped precursor to a future general secrets vault — plaintext, same trust
/// model as the rest of this app's local SQLite data.</summary>
public class AIProviderConfig
{
    public int Id { get; set; }
    public required string ProviderKey { get; set; }
    public required string FieldName { get; set; }
    public string? Value { get; set; }
}
