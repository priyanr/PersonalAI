namespace PersonalAI.Core.Models.Settings;

public class OllamaSettings
{
    public const string SectionName = "Ollama";

    public required string BaseUrl { get; set; }
    public string DefaultSystemPrompt { get; set; } = "You are a helpful assistant with access to tools. Use them when they help answer the user's request.";
    public int RequestTimeoutSeconds { get; set; } = 300;
}
