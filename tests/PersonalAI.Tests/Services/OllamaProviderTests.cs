using FluentAssertions;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.AI.Ollama;
using Microsoft.Extensions.Options;

namespace PersonalAI.Tests.Services;

public class OllamaProviderTests
{
    private static OllamaProvider CreateProvider(string responseBody) => CreateProvider(responseBody, out _);

    private static OllamaProvider CreateProvider(string responseBody, out FakeHttpMessageHandler handler)
    {
        handler = new FakeHttpMessageHandler(responseBody);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var cache = new AIProviderConfigCache();
        cache.SetProvider("ollama", new Dictionary<string, string?> { ["BaseUrl"] = "http://localhost:11434" });
        var agentSettings = Options.Create(new AgentSettings());
        return new OllamaProvider(httpClient, cache, agentSettings);
    }

    [Fact]
    public async Task ChatStreamAsync_AccumulatesTextDeltasAndStopsAtDone()
    {
        var ndjson =
            """{"message":{"role":"assistant","content":"Hel"},"done":false}""" + "\n" +
            """{"message":{"role":"assistant","content":"lo"},"done":false}""" + "\n" +
            """{"message":{"role":"assistant","content":""},"done":true}""" + "\n";

        var provider = CreateProvider(ndjson);
        var chunks = new List<Core.Models.DTOs.AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("llama3.1:8b", [], [], null, false, null, CancellationToken.None))
            chunks.Add(chunk);

        chunks.Should().HaveCount(3);
        string.Concat(chunks.Take(2).Select(c => c.ContentDelta)).Should().Be("Hello");
        chunks[^1].Done.Should().BeTrue();
    }

    [Fact]
    public async Task ChatStreamAsync_DoneChunkEvalCounts_AreReportedAsUsage()
    {
        var ndjson =
            """{"message":{"role":"assistant","content":"Hi"},"done":false}""" + "\n" +
            """{"message":{"role":"assistant","content":""},"done":true,"prompt_eval_count":120,"eval_count":15}""" + "\n";

        var provider = CreateProvider(ndjson);
        var chunks = new List<Core.Models.DTOs.AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("llama3.1:8b", [], [], null, false, null, CancellationToken.None))
            chunks.Add(chunk);

        chunks[0].Usage.Should().BeNull();
        chunks[^1].Usage.Should().Be(new Core.Models.DTOs.AiTokenUsage(120, 15));
    }

    [Fact]
    public async Task ChatStreamAsync_ParsesToolCallsFromTerminatingChunk()
    {
        var ndjson =
            """{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"web_search","arguments":{"query":"weather in Paris"}}}]},"done":true}""" + "\n";

        var provider = CreateProvider(ndjson);
        var chunks = new List<Core.Models.DTOs.AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("llama3.1:8b", [], [], null, false, null, CancellationToken.None))
            chunks.Add(chunk);

        var toolCalls = chunks[^1].ToolCalls;
        toolCalls.Should().ContainSingle();
        toolCalls![0].Name.Should().Be("web_search");
        toolCalls[0].ArgumentsJson.Should().Contain("weather in Paris");
        toolCalls[0].ToolCallId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ChatStreamAsync_WithNumCtx_SendsItInRequestOptions()
    {
        var ndjson = """{"message":{"role":"assistant","content":"hi"},"done":true}""" + "\n";

        var provider = CreateProvider(ndjson, out var handler);
        await foreach (var _ in provider.ChatStreamAsync("llama3.1:8b", [], [], 8192, false, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain("\"num_ctx\":8192");
    }

    [Fact]
    public async Task ChatStreamAsync_WithoutNumCtx_OmitsOptions()
    {
        var ndjson = """{"message":{"role":"assistant","content":"hi"},"done":true}""" + "\n";

        var provider = CreateProvider(ndjson, out var handler);
        await foreach (var _ in provider.ChatStreamAsync("llama3.1:8b", [], [], null, false, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().NotContain("num_ctx");
    }

    [Fact]
    public async Task ListModelsAsync_ParsesTagsResponse()
    {
        var json = """
        {
          "models": [
            { "name": "llama3.1:8b", "size": 4700000000, "digest": "abc123", "modified_at": "2024-01-01T00:00:00Z",
              "details": { "parameter_size": "8B", "quantization_level": "Q4_0" } }
          ]
        }
        """;

        var provider = CreateProvider(json);
        var models = await provider.ListModelsAsync(CancellationToken.None);

        models.Should().ContainSingle();
        models[0].Name.Should().Be("llama3.1:8b");
        models[0].ParameterSize.Should().Be("8B");
        models[0].QuantizationLevel.Should().Be("Q4_0");
    }

    [Fact]
    public async Task PullModelStreamAsync_StopsAtSuccessStatus()
    {
        var ndjson =
            """{"status":"pulling manifest"}""" + "\n" +
            """{"status":"downloading digest","digest":"sha256:abc","total":100,"completed":50}""" + "\n" +
            """{"status":"success"}""" + "\n";

        var provider = CreateProvider(ndjson);
        var progress = new List<Core.Models.DTOs.AiPullProgress>();
        await foreach (var p in provider.PullModelStreamAsync("llama3.1:8b", CancellationToken.None))
            progress.Add(p);

        progress.Should().HaveCount(3);
        progress[^1].Status.Should().Be("success");
    }

    [Fact]
    public async Task ChatStreamAsync_ThinkingField_IsYieldedAsReasoningDelta()
    {
        var ndjson =
            """{"message":{"role":"assistant","content":"","thinking":"Hmm, "},"done":false}""" + "\n" +
            """{"message":{"role":"assistant","content":"","thinking":"ok."},"done":false}""" + "\n" +
            """{"message":{"role":"assistant","content":"Hi"},"done":false}""" + "\n" +
            """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}""" + "\n";

        var provider = CreateProvider(ndjson);
        var chunks = new List<Core.Models.DTOs.AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("qwen3:8b", [], [], null, true, null, CancellationToken.None))
            chunks.Add(chunk);

        string.Concat(chunks.Select(c => c.ReasoningDelta)).Should().Be("Hmm, ok.");
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("Hi");
        chunks[^1].HitOutputLimit.Should().BeFalse();
    }

    [Fact]
    public async Task ChatStreamAsync_DoneReasonLength_FlagsHitOutputLimit()
    {
        var ndjson = """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"length"}""" + "\n";

        var provider = CreateProvider(ndjson);
        var chunks = new List<Core.Models.DTOs.AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("qwen3:8b", [], [], null, true, null, CancellationToken.None))
            chunks.Add(chunk);

        chunks[^1].HitOutputLimit.Should().BeTrue();
    }

    [Fact]
    public async Task ChatStreamAsync_ProviderMaxOutputTokensOverride_IsSentAsNumPredict()
    {
        var handler = new FakeHttpMessageHandler("""{"message":{"role":"assistant","content":"hi"},"done":true}""" + "\n");
        var cache = new AIProviderConfigCache();
        cache.SetProvider("ollama", new Dictionary<string, string?> { ["BaseUrl"] = "http://localhost:11434", ["MaxOutputTokens"] = "20000" });
        var provider = new OllamaProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings { MaxOutputTokens = 8192 }));

        await foreach (var _ in provider.ChatStreamAsync("qwen3:8b", [], [], null, true, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain("\"num_predict\":20000");
        handler.LastRequestBody.Should().NotContain("\"thinking\"");
    }

    [Fact]
    public async Task ChatStreamAsync_NoConfiguredBaseUrl_FallsBackToDefault()
    {
        var handler = new FakeHttpMessageHandler("""{"message":{"role":"assistant","content":"hi"},"done":true}""" + "\n");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(OllamaProvider.DefaultBaseUrl) };
        var provider = new OllamaProvider(httpClient, new AIProviderConfigCache(), Options.Create(new AgentSettings()));

        await foreach (var _ in provider.ChatStreamAsync("llama3.1:8b", [], [], null, false, null, CancellationToken.None)) { }

        handler.LastRequestUri!.ToString().Should().StartWith(OllamaProvider.DefaultBaseUrl);
    }

    [Fact]
    public async Task ChatStreamAsync_NoThinkingPreference_SendsThinkFalse()
    {
        var provider = CreateProvider("""{"message":{"role":"assistant","content":"hi"},"done":true}""" + "\n", out var handler);

        await foreach (var _ in provider.ChatStreamAsync("qwen3:8b", [], [], null, null, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain("\"think\":false");
    }

    [Theory]
    [InlineData(null, "high", "\"think\":\"high\"")]
    [InlineData(true, "minimal", "\"think\":\"low\"")]
    [InlineData(true, "medium", "\"think\":\"medium\"")]
    [InlineData(true, "xhigh", "\"think\":\"high\"")]
    [InlineData(true, "none", "\"think\":false")]
    [InlineData(false, "high", "\"think\":false")]
    [InlineData(true, null, "\"think\":true")]
    public async Task ChatStreamAsync_ReasoningEffort_IsSentAsAThinkLevel(bool? think, string? effort, string expected)
    {
        var provider = CreateProvider("""{"message":{"role":"assistant","content":"hi"},"done":true}""" + "\n", out var handler);

        await foreach (var _ in provider.ChatStreamAsync("gpt-oss:20b", [], [], null, think, effort, CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain(expected);
    }

    [Fact]
    public async Task ChatStreamAsync_ModelThinkingPinnedOff_WinsOverReasoningEffort()
    {
        var handler = new FakeHttpMessageHandler("""{"message":{"role":"assistant","content":"hi"},"done":true}""" + "\n");
        var cache = new AIProviderConfigCache();
        cache.SetModel("ollama", "gpt-oss:20b", new Dictionary<string, string?> { [ModelSettings.ThinkingField] = ModelSettings.AlwaysOff });
        var provider = new OllamaProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings()));

        await foreach (var _ in provider.ChatStreamAsync("gpt-oss:20b", [], [], null, true, "high", CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain("\"think\":false");
    }
}
