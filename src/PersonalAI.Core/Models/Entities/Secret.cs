namespace PersonalAI.Core.Models.Entities;

/// <summary>A user-defined key/value secret (e.g. an API token a future skill can read), stored in the
/// same plaintext trust model as AIProviderConfig — not a general-purpose hardened vault.</summary>
public class Secret
{
    public int Id { get; set; }
    public required string Key { get; set; }
    public string? Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
