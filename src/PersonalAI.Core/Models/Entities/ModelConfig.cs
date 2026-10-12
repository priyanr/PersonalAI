namespace PersonalAI.Core.Models.Entities;

/// <summary>One per-model setting for one AI provider's model (e.g. ProviderKey="azure",
/// ModelName="gpt-6-astra", FieldName="OutputLimitParameter"). Same key/value shape as AIProviderConfig,
/// one level down: it pins request capabilities a model name can't reveal.</summary>
public class ModelConfig
{
    public int Id { get; set; }
    public required string ProviderKey { get; set; }
    public required string ModelName { get; set; }
    public required string FieldName { get; set; }
    public string? Value { get; set; }
}
