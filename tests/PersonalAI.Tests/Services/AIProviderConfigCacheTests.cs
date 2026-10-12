using FluentAssertions;
using PersonalAI.Core.Services.AI;

namespace PersonalAI.Tests.Services;

public class AIProviderConfigCacheTests
{
    [Fact]
    public void GetValue_UnknownProvider_ReturnsNull()
    {
        var cache = new AIProviderConfigCache();
        cache.GetValue("openai", "ApiKey").Should().BeNull();
    }

    [Fact]
    public void SetProvider_ThenGetValue_RoundTrips()
    {
        var cache = new AIProviderConfigCache();
        cache.SetProvider("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk-abc", ["BaseUrl"] = "https://api.openai.com/v1" });

        cache.GetValue("openai", "ApiKey").Should().Be("sk-abc");
        cache.GetValue("openai", "BaseUrl").Should().Be("https://api.openai.com/v1");
        cache.GetValue("openai", "Unknown").Should().BeNull();
    }

    [Fact]
    public void SetProvider_Twice_ReplacesWholeMapRatherThanMerging()
    {
        var cache = new AIProviderConfigCache();
        cache.SetProvider("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk-old", ["BaseUrl"] = "https://old" });
        cache.SetProvider("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk-new" });

        cache.GetValue("openai", "ApiKey").Should().Be("sk-new");
        cache.GetValue("openai", "BaseUrl").Should().BeNull();
    }

    [Fact]
    public void GetValue_IsCaseInsensitiveOnProviderKey()
    {
        var cache = new AIProviderConfigCache();
        cache.SetProvider("Ollama", new Dictionary<string, string?> { ["BaseUrl"] = "http://localhost:11434" });

        cache.GetValue("ollama", "BaseUrl").Should().Be("http://localhost:11434");
    }
}
