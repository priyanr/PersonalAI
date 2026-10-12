using FluentAssertions;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.AI;

namespace PersonalAI.Tests.Services;

public class AIProviderRegistryTests
{
    private sealed class StubProvider(string key) : IAIProvider
    {
        public string Key => key;
        public string DisplayName => key;
        public IReadOnlyList<ProviderConfigField> ConfigFields => [];
        public IAsyncEnumerable<AiChatChunk> ChatStreamAsync(string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools, int? numCtx, bool? think, string? reasoningEffort, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void Resolve_KnownKey_ReturnsProvider()
    {
        var registry = new AIProviderRegistry([new StubProvider("ollama"), new StubProvider("openai")]);
        registry.Resolve("openai").Key.Should().Be("openai");
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var registry = new AIProviderRegistry([new StubProvider("ollama")]);
        registry.Resolve("OLLAMA").Key.Should().Be("ollama");
    }

    [Fact]
    public void Resolve_UnknownKey_Throws()
    {
        var registry = new AIProviderRegistry([new StubProvider("ollama")]);
        var act = () => registry.Resolve("does-not-exist");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryResolve_UnknownKey_ReturnsFalse()
    {
        var registry = new AIProviderRegistry([new StubProvider("ollama")]);
        registry.TryResolve("does-not-exist", out var provider).Should().BeFalse();
        provider.Should().BeNull();
    }

    [Fact]
    public void All_ReturnsEveryRegisteredProvider()
    {
        var registry = new AIProviderRegistry([new StubProvider("ollama"), new StubProvider("openai"), new StubProvider("deepseek")]);
        registry.All.Select(p => p.Key).Should().BeEquivalentTo(["ollama", "openai", "deepseek"]);
    }
}
