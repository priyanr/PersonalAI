using System.Net;
using System.Text;
using FluentAssertions;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.AI.OpenAiCompatible;
using Microsoft.Extensions.Options;

namespace PersonalAI.Tests.Services;

/// <summary>Exercises OpenAiCompatibleProviderBase through the OpenAI subclass — DeepSeek shares
/// the exact same wire-protocol code path, so it isn't re-tested separately.</summary>
public class OpenAiCompatibleProviderTests
{
    private static OpenAiProvider CreateProvider(string sseBody, out FakeHttpMessageHandler handler, string? apiKey = "sk-test")
    {
        handler = new FakeHttpMessageHandler(sseBody);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1") };
        var cache = new AIProviderConfigCache();
        var values = new Dictionary<string, string?> { ["BaseUrl"] = "https://api.openai.com/v1" };
        if (apiKey is not null) values["ApiKey"] = apiKey;
        cache.SetProvider("openai", values);
        return new OpenAiProvider(httpClient, cache, Options.Create(new AgentSettings()));
    }

    [Fact]
    public async Task ChatStreamAsync_AccumulatesTextDeltasAndYieldsTerminalChunkOnFinishReason()
    {
        var sse =
            """data: {"choices":[{"delta":{"content":"Hel"}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"content":"lo"}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""" + "\n\n" +
            "data: [DONE]\n\n";

        var provider = CreateProvider(sse, out _);
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("gpt-4o-mini", [], [], null, false, null, CancellationToken.None))
            chunks.Add(chunk);

        chunks.Should().HaveCount(3);
        string.Concat(chunks.Take(2).Select(c => c.ContentDelta)).Should().Be("Hello");
        chunks[^1].Done.Should().BeTrue();
        chunks[^1].ToolCalls.Should().BeNull();
    }

    [Fact]
    public async Task ChatStreamAsync_UsageChunkAfterFinishReason_IsReportedOnTerminalChunk()
    {
        var sse =
            """data: {"choices":[{"delta":{"content":"Hi"}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""" + "\n\n" +
            """data: {"choices":[],"usage":{"prompt_tokens":42,"completion_tokens":7,"total_tokens":49}}""" + "\n\n" +
            "data: [DONE]\n\n";

        var provider = CreateProvider(sse, out var handler);
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("gpt-4o-mini", [], [], null, false, null, CancellationToken.None))
            chunks.Add(chunk);

        chunks[^1].Done.Should().BeTrue();
        chunks[^1].Usage.Should().Be(new AiTokenUsage(42, 7));
        handler.LastRequestBody.Should().Contain("\"include_usage\":true");
    }

    [Fact]
    public async Task ChatStreamAsync_NoUsageReported_TerminalChunkUsageIsNull()
    {
        var sse = """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""" + "\n\n" + "data: [DONE]\n\n";

        var provider = CreateProvider(sse, out _);
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("gpt-4o-mini", [], [], null, false, null, CancellationToken.None))
            chunks.Add(chunk);

        chunks[^1].Usage.Should().BeNull();
    }

    [Fact]
    public async Task ChatStreamAsync_ReassemblesMultiFragmentToolCallsAcrossIndicesIntoOneTerminalChunk()
    {
        var sse =
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_abc","function":{"name":"web_search","arguments":""}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"query\":"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"weather\"}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_def","function":{"name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\n" +
            "data: [DONE]\n\n";

        var provider = CreateProvider(sse, out _);
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("gpt-4o-mini", [], [], null, false, null, CancellationToken.None))
            chunks.Add(chunk);

        chunks.Should().ContainSingle(c => c.ToolCalls != null && c.ToolCalls.Count > 0);
        var toolCalls = chunks.Single(c => c.ToolCalls != null && c.ToolCalls.Count > 0).ToolCalls!;
        toolCalls.Should().HaveCount(2);

        toolCalls[0].ToolCallId.Should().Be("call_abc");
        toolCalls[0].Name.Should().Be("web_search");
        toolCalls[0].ArgumentsJson.Should().Be("""{"query":"weather"}""");

        toolCalls[1].ToolCallId.Should().Be("call_def");
        toolCalls[1].Name.Should().Be("read_file");
        toolCalls[1].ArgumentsJson.Should().Be("""{"path":"a.txt"}""");
    }

    [Fact]
    public async Task ChatStreamAsync_SendsBearerAuthorizationHeaderFromConfiguredApiKey()
    {
        var sse = """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""" + "\n\n" + "data: [DONE]\n\n";

        var provider = CreateProvider(sse, out var handler, apiKey: "sk-secret");
        await foreach (var _ in provider.ChatStreamAsync("gpt-4o-mini", [], [], null, false, null, CancellationToken.None)) { }

        handler.LastAuthorizationHeader.Should().Be("Bearer sk-secret");
    }

    [Fact]
    public async Task ListModelsAsync_ParsesModelsListResponse()
    {
        var json = """{"data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"}]}""";

        var provider = CreateProvider(json, out _);
        var models = await provider.ListModelsAsync(CancellationToken.None);

        models.Select(m => m.Name).Should().BeEquivalentTo(["gpt-4o", "gpt-4o-mini"]);
    }

    [Fact]
    public void PullModelStreamAsync_ThrowsNotSupported()
    {
        var provider = CreateProvider("", out _);
        var act = () => provider.PullModelStreamAsync("gpt-4o", CancellationToken.None);

        act.Should().Throw<NotSupportedException>();
    }

    private const string ToolsNeedNoReasoningError =
        """{"error":{"message":"Function tools with reasoning_effort are not supported for gpt-5.6-sol in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.","type":"invalid_request_error","param":"reasoning_effort","code":null}}""";

    private const string StopSse =
        """data: {"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}""" + "\n\n" + "data: [DONE]\n\n";

    private static readonly AiToolDefinition SampleTool =
        new("get_time", "Get the time", """{"type":"object","properties":{}}""");

    private static OpenAiProvider CreateProvider(SequencedHttpMessageHandler handler)
    {
        var cache = new AIProviderConfigCache();
        cache.SetProvider("openai", new Dictionary<string, string?> { ["BaseUrl"] = "https://api.openai.com/v1", ["ApiKey"] = "sk-test" });
        return new OpenAiProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings()));
    }

    [Fact]
    public async Task ChatStreamAsync_RetriesWithReasoningEffortNone_WhenModelRejectsToolsWithReasoning()
    {
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.BadRequest, ToolsNeedNoReasoningError), (HttpStatusCode.OK, StopSse), (HttpStatusCode.OK, StopSse));
        var provider = CreateProvider(handler);

        var text = new StringBuilder();
        await foreach (var chunk in provider.ChatStreamAsync("retry-model-a", [], [SampleTool], null, false, null, CancellationToken.None))
            text.Append(chunk.ContentDelta);

        text.ToString().Should().Be("ok");
        handler.RequestBodies.Should().HaveCount(2);
        handler.RequestBodies[0].Should().NotContain("reasoning_effort");
        handler.RequestBodies[1].Should().Contain("\"reasoning_effort\":\"none\"");

        // Remembered: the next call to the same model sends "none" up front, no failed round trip.
        await foreach (var _ in provider.ChatStreamAsync("retry-model-a", [], [SampleTool], null, false, null, CancellationToken.None)) { }
        handler.RequestBodies.Should().HaveCount(3);
        handler.RequestBodies[2].Should().Contain("\"reasoning_effort\":\"none\"");
    }

    [Fact]
    public async Task ChatStreamAsync_DoesNotRetry_OnUnrelatedBadRequest()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.BadRequest, """{"error":{"message":"bad things"}}"""));
        var provider = CreateProvider(handler);

        var act = async () =>
        {
            await foreach (var _ in provider.ChatStreamAsync("retry-model-b", [], [SampleTool], null, false, null, CancellationToken.None)) { }
        };

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("bad things");
        handler.RequestBodies.Should().ContainSingle();
    }

    [Fact]
    public async Task ChatStreamAsync_NeverSendsReasoningEffort_WithoutTools()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, StopSse));
        var provider = CreateProvider(handler);

        await foreach (var _ in provider.ChatStreamAsync("retry-model-c", [], [], null, false, null, CancellationToken.None)) { }

        handler.RequestBodies.Single().Should().NotContain("reasoning_effort");
    }

    [Fact]
    public async Task ChatStreamAsync_ReasoningContentAndReasoningDeltas_AreYieldedSeparatelyFromContent()
    {
        var sse =
            """data: {"choices":[{"delta":{"reasoning_content":"Let me "}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"reasoning":"think."}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"content":"Answer"}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""" + "\n\n" +
            "data: [DONE]\n\n";

        var provider = CreateProvider(sse, out _);
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("deepseek-reasoner", [], [], null, true, null, CancellationToken.None))
            chunks.Add(chunk);

        string.Concat(chunks.Select(c => c.ReasoningDelta)).Should().Be("Let me think.");
        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("Answer");
        chunks.Where(c => c.ReasoningDelta is not null).Should().OnlyContain(c => c.ContentDelta == null);
        chunks[^1].HitOutputLimit.Should().BeFalse();
    }

    [Fact]
    public async Task ChatStreamAsync_FinishReasonLength_FlagsTerminalChunkAsHitOutputLimit()
    {
        var sse =
            """data: {"choices":[{"delta":{"reasoning_content":"thinking forever"}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"length"}]}""" + "\n\n" +
            """data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":8192}}""" + "\n\n" +
            "data: [DONE]\n\n";

        var provider = CreateProvider(sse, out _);
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("deepseek-reasoner", [], [], null, true, null, CancellationToken.None))
            chunks.Add(chunk);

        chunks[^1].Done.Should().BeTrue();
        chunks[^1].HitOutputLimit.Should().BeTrue();
        chunks[^1].Usage.Should().Be(new AiTokenUsage(10, 8192));
    }

    [Fact]
    public async Task ChatStreamAsync_ProviderMaxOutputTokensOverride_ReplacesAgentDefault()
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var cache = new AIProviderConfigCache();
        cache.SetProvider("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk", ["BaseUrl"] = "http://localhost:1234/v1", ["MaxOutputTokens"] = "32000" });
        var provider = new OpenAiProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings { MaxOutputTokens = 8192 }));

        await foreach (var _ in provider.ChatStreamAsync("gpt-4o-mini", [], [], null, false, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain("\"max_tokens\":32000");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("lots")]
    public async Task ChatStreamAsync_BlankOrInvalidMaxOutputTokensOverride_FallsBackToAgentDefault(string? configured)
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var cache = new AIProviderConfigCache();
        cache.SetProvider("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk", ["BaseUrl"] = "http://localhost:1234/v1", ["MaxOutputTokens"] = configured });
        var provider = new OpenAiProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings { MaxOutputTokens = 8192 }));

        await foreach (var _ in provider.ChatStreamAsync("gpt-4o-mini", [], [], null, false, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain("\"max_tokens\":8192");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    public async Task DeepSeek_SendsThinkingDisabledOnlyWhenThinkingIsExplicitlyOff(bool? think, bool expectDisabled)
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var cache = new AIProviderConfigCache();
        cache.SetProvider("deepseek", new Dictionary<string, string?> { ["ApiKey"] = "sk" });
        var provider = new DeepSeekProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings()));

        await foreach (var _ in provider.ChatStreamAsync("deepseek-chat", [], [], null, think, null, CancellationToken.None)) { }

        if (expectDisabled)
            handler.LastRequestBody.Should().Contain("\"thinking\":{\"type\":\"disabled\"}");
        else
            handler.LastRequestBody.Should().NotContain("\"thinking\"");
    }

    [Fact]
    public async Task OpenAi_NeverSendsDeepSeekThinkingParameter()
    {
        var provider = CreateProvider(StopSse, out var handler);

        await foreach (var _ in provider.ChatStreamAsync("gpt-4o-mini", [], [], null, false, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().NotContain("\"thinking\"");
    }

    [Fact]
    public async Task ChatStreamAsync_StructuredReasoningField_DoesNotDropTheChunksContentOrToolCalls()
    {
        var sse =
            """data: {"choices":[{"delta":{"reasoning":{"summary":"x"},"content":"Hi"}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{"reasoning":["a"],"tool_calls":[{"index":0,"id":"c1","function":{"name":"t","arguments":"{}"}}]},"finish_reason":"length"}]}""" + "\n\n" +
            "data: [DONE]\n\n";

        var provider = CreateProvider(sse, out _);
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("gpt-4o-mini", [], [], null, null, null, CancellationToken.None))
            chunks.Add(chunk);

        chunks.Should().Contain(c => c.ContentDelta == "Hi");
        chunks.Should().OnlyContain(c => c.ReasoningDelta == null);
        chunks[^1].ToolCalls.Should().ContainSingle(t => t.Name == "t");
        chunks[^1].HitOutputLimit.Should().BeTrue();
    }

    [Fact]
    public async Task ChatStreamAsync_ToolCallCutOffBeforeArguments_KeepsBlankArgumentsInsteadOfDefaultingToEmptyObject()
    {
        var sse =
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"write_file"}}]}}]}""" + "\n\n" +
            """data: {"choices":[{"delta":{},"finish_reason":"length"}]}""" + "\n\n" +
            "data: [DONE]\n\n";

        var provider = CreateProvider(sse, out _);
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in provider.ChatStreamAsync("gpt-4o-mini", [], [], null, null, null, CancellationToken.None))
            chunks.Add(chunk);

        chunks[^1].ToolCalls!.Single().ArgumentsJson.Should().BeEmpty();
    }

    [Fact]
    public async Task DeepSeek_SendsAssistantReasoningContentBack()
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var cache = new AIProviderConfigCache();
        cache.SetProvider("deepseek", new Dictionary<string, string?> { ["ApiKey"] = "sk" });
        var provider = new DeepSeekProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings()));
        AiChatMessage[] messages =
        [
            new("user", "go"),
            new("assistant", "", [new AiToolCall("c1", "t", "{}")], ReasoningContent: "because"),
            new("tool", "ok", null, "c1")
        ];

        await foreach (var _ in provider.ChatStreamAsync("deepseek-reasoner", messages, [], null, true, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain("\"reasoning_content\":\"because\"");
    }

    [Fact]
    public async Task OpenAi_NeverSendsReasoningContentBack()
    {
        var provider = CreateProvider(StopSse, out var handler);
        AiChatMessage[] messages =
        [
            new("user", "go"),
            new("assistant", "", [new AiToolCall("c1", "t", "{}")], ReasoningContent: "because"),
            new("tool", "ok", null, "c1")
        ];

        await foreach (var _ in provider.ChatStreamAsync("gpt-4o-mini", messages, [], null, true, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().NotContain("reasoning_content");
    }

    private const string UseMaxCompletionTokensError =
        """{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.","type":"invalid_request_error","param":"max_tokens","code":"unsupported_parameter"}}""";

    private const string MaxCompletionTokensUnrecognizedError =
        """{"error":{"code":"BadRequestForDependentService","message":"Extra inputs are not permitted: max_completion_tokens"}}""";

    private static AzureAiProvider CreateAzureProvider(HttpMessageHandler handler, out AIProviderConfigCache cache)
    {
        cache = new AIProviderConfigCache();
        cache.SetProvider("azure", new Dictionary<string, string?> { ["BaseUrl"] = "https://my-resource.openai.azure.com", ["ApiKey"] = "k" });
        return new AzureAiProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings { MaxOutputTokens = 4096 }));
    }

    [Fact]
    public async Task Azure_UnknownModelRejectingMaxTokens_RetriesWithMaxCompletionTokensAndRemembers()
    {
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.BadRequest, UseMaxCompletionTokensError), (HttpStatusCode.OK, StopSse), (HttpStatusCode.OK, StopSse));
        var provider = CreateAzureProvider(handler, out _);

        var text = new StringBuilder();
        await foreach (var chunk in provider.ChatStreamAsync("gpt-6-astra-heal", [], [], null, null, null, CancellationToken.None))
            text.Append(chunk.ContentDelta);

        text.ToString().Should().Be("ok");
        handler.RequestBodies.Should().HaveCount(2);
        handler.RequestBodies[0].Should().Contain("\"max_tokens\":4096").And.NotContain("max_completion_tokens");
        handler.RequestBodies[1].Should().Contain("\"max_completion_tokens\":4096").And.NotContain("\"max_tokens\"");

        await foreach (var _ in provider.ChatStreamAsync("gpt-6-astra-heal", [], [], null, null, null, CancellationToken.None)) { }
        handler.RequestBodies.Should().HaveCount(3);
        handler.RequestBodies[2].Should().Contain("\"max_completion_tokens\":4096");
    }

    [Fact]
    public async Task Azure_ReasoningNamedModelRejectingMaxCompletionTokens_RetriesWithMaxTokens()
    {
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.BadRequest, MaxCompletionTokensUnrecognizedError), (HttpStatusCode.OK, StopSse));
        var provider = CreateAzureProvider(handler, out _);

        await foreach (var _ in provider.ChatStreamAsync("o3-proxy-claude-heal", [], [], null, null, null, CancellationToken.None)) { }

        handler.RequestBodies.Should().HaveCount(2);
        handler.RequestBodies[0].Should().Contain("\"max_completion_tokens\":4096");
        handler.RequestBodies[1].Should().Contain("\"max_tokens\":4096").And.NotContain("max_completion_tokens");
    }

    [Fact]
    public async Task ChatStreamAsync_OutputParameterAndReasoningErrors_AreBothFixedInOneCall()
    {
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.BadRequest, UseMaxCompletionTokensError),
            (HttpStatusCode.BadRequest, ToolsNeedNoReasoningError),
            (HttpStatusCode.OK, StopSse));
        var provider = CreateAzureProvider(handler, out _);

        await foreach (var _ in provider.ChatStreamAsync("gpt-6-astra-both", [], [SampleTool], null, null, null, CancellationToken.None)) { }

        handler.RequestBodies.Should().HaveCount(3);
        handler.RequestBodies[2].Should().Contain("\"max_completion_tokens\"").And.Contain("\"reasoning_effort\":\"none\"");
    }

    [Fact]
    public async Task ChatStreamAsync_PinnedOutputParameter_IsUsedAndNeverSwapped()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.BadRequest, UseMaxCompletionTokensError));
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "gpt-6-astra-pinned", new Dictionary<string, string?> { [ModelSettings.OutputLimitParameterField] = "max_tokens" });

        var act = async () =>
        {
            await foreach (var _ in provider.ChatStreamAsync("gpt-6-astra-pinned", [], [], null, null, null, CancellationToken.None)) { }
        };

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("max_completion_tokens");
        handler.RequestBodies.Should().ContainSingle().Which.Should().Contain("\"max_tokens\":4096");
    }

    [Theory]
    [InlineData("max_completion_tokens", "\"max_completion_tokens\":4096", "\"max_tokens\"")]
    [InlineData("max_tokens", "\"max_tokens\":4096", "max_completion_tokens")]
    public async Task ChatStreamAsync_PinnedOutputParameter_OverridesNameHeuristic(string pinned, string expected, string absent)
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "gpt-5-pin-" + pinned, new Dictionary<string, string?> { [ModelSettings.OutputLimitParameterField] = pinned });

        await foreach (var _ in provider.ChatStreamAsync("gpt-5-pin-" + pinned, [], [], null, null, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain(expected).And.NotContain(absent);
    }

    [Fact]
    public async Task ChatStreamAsync_OutputParameterNone_SendsNeitherField()
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "no-cap", new Dictionary<string, string?> { [ModelSettings.OutputLimitParameterField] = "none" });

        await foreach (var _ in provider.ChatStreamAsync("no-cap", [], [], null, null, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().NotContain("max_tokens").And.NotContain("max_completion_tokens");
    }

    [Fact]
    public async Task ChatStreamAsync_ModelMaxOutputTokens_OverridesProviderAndAgentDefault()
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var cache = new AIProviderConfigCache();
        cache.SetProvider("openai", new Dictionary<string, string?> { ["ApiKey"] = "sk", ["BaseUrl"] = "http://localhost:1234/v1", ["MaxOutputTokens"] = "32000" });
        cache.SetModel("openai", "big-model", new Dictionary<string, string?> { ["MaxOutputTokens"] = "64000" });
        var provider = new OpenAiProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings { MaxOutputTokens = 8192 }));

        await foreach (var _ in provider.ChatStreamAsync("big-model", [], [], null, false, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain("\"max_tokens\":64000");
    }

    [Fact]
    public async Task ChatStreamAsync_ToolCallingDisabled_SendsNoTools()
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "no-tools", new Dictionary<string, string?> { [ModelSettings.ToolCallingField] = "disabled" });

        await foreach (var _ in provider.ChatStreamAsync("no-tools", [], [SampleTool], null, null, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().NotContain("\"tools\":[");
    }

    [Fact]
    public async Task ChatStreamAsync_PinnedReasoningEffort_IsSentEvenWithoutTools()
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "effort-model", new Dictionary<string, string?> { [ModelSettings.ReasoningEffortField] = "low" });

        await foreach (var _ in provider.ChatStreamAsync("effort-model", [], [], null, null, null, CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain("\"reasoning_effort\":\"low\"");
    }

    [Theory]
    [InlineData("off", true, true)]
    [InlineData("on", false, false)]
    [InlineData("auto", false, true)]
    public async Task DeepSeek_ModelThinkingOverride_ReplacesSessionSetting(string setting, bool sessionThink, bool expectDisabled)
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var cache = new AIProviderConfigCache();
        cache.SetProvider("deepseek", new Dictionary<string, string?> { ["ApiKey"] = "sk" });
        cache.SetModel("deepseek", "deepseek-chat", new Dictionary<string, string?> { [ModelSettings.ThinkingField] = setting });
        var provider = new DeepSeekProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings()));

        await foreach (var _ in provider.ChatStreamAsync("deepseek-chat", [], [], null, sessionThink, null, CancellationToken.None)) { }

        if (expectDisabled)
            handler.LastRequestBody.Should().Contain("\"thinking\":{\"type\":\"disabled\"}");
        else
            handler.LastRequestBody.Should().NotContain("\"thinking\"");
    }

    private const string ReasoningNoneRejectedError =
        """{"error":{"message":"Unsupported value: 'reasoning_effort' does not support 'none' with this model. Supported values are: 'low', 'medium', 'high', and 'xhigh'.","type":"invalid_request_error","param":"reasoning_effort","code":"unsupported_value"}}""";

    private const string ResponsesTextSse =
        """data: {"type":"response.created","response":{"status":"in_progress"}}""" + "\n\n" +
        """data: {"type":"response.output_text.delta","output_index":0,"delta":"o"}""" + "\n\n" +
        """data: {"type":"response.output_text.delta","output_index":0,"delta":"k"}""" + "\n\n" +
        """data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":12,"output_tokens":3}}}""" + "\n\n";

    private static async Task<List<AiChatChunk>> CollectAsync(IAsyncEnumerable<AiChatChunk> stream)
    {
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in stream) chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public async Task Azure_ModelRejectingToolsWithReasoningAndReasoningNone_SwitchesToResponsesApiAndRemembers()
    {
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.BadRequest, ToolsNeedNoReasoningError),
            (HttpStatusCode.BadRequest, ReasoningNoneRejectedError),
            (HttpStatusCode.OK, ResponsesTextSse));
        var provider = CreateAzureProvider(handler, out _);

        var chunks = await CollectAsync(provider.ChatStreamAsync("astra-switch", [new AiChatMessage("user", "hi")], [SampleTool], null, null, null, CancellationToken.None));

        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("ok");
        chunks[^1].Done.Should().BeTrue();
        chunks[^1].Usage.Should().Be(new AiTokenUsage(12, 3));
        handler.RequestUris.Should().Equal(
            "https://my-resource.openai.azure.com/openai/v1/chat/completions",
            "https://my-resource.openai.azure.com/openai/v1/chat/completions",
            "https://my-resource.openai.azure.com/openai/v1/responses");
        handler.RequestBodies[1].Should().Contain("\"reasoning_effort\":\"none\"");
        handler.RequestBodies[2].Should().NotContain("reasoning_effort").And.NotContain("\"effort\"");

        // Remembered: the next call goes straight to /responses.
        await CollectAsync(provider.ChatStreamAsync("astra-switch", [], [SampleTool], null, null, null, CancellationToken.None));
        handler.RequestUris.Should().HaveCount(4);
        handler.RequestUris[3].Should().EndWith("/responses");
    }

    [Fact]
    public async Task Azure_PinnedEffortRefusedWithTools_SwitchesToResponsesApiKeepingTheEffort()
    {
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.BadRequest, ToolsNeedNoReasoningError), (HttpStatusCode.OK, ResponsesTextSse));
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "astra-pinned-effort", new Dictionary<string, string?> { [ModelSettings.ReasoningEffortField] = "high" });

        await CollectAsync(provider.ChatStreamAsync("astra-pinned-effort", [], [SampleTool], null, null, null, CancellationToken.None));

        handler.RequestUris.Should().HaveCount(2);
        handler.RequestUris[1].Should().EndWith("/responses");
        handler.RequestBodies[1].Should().Contain("\"reasoning\":{\"effort\":\"high\",\"summary\":\"auto\"}");
    }

    [Fact]
    public async Task Azure_ApiPinnedToChatCompletions_NeverSwitches()
    {
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.BadRequest, ToolsNeedNoReasoningError), (HttpStatusCode.BadRequest, ReasoningNoneRejectedError));
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "astra-chat-only", new Dictionary<string, string?> { [ModelSettings.ApiField] = "chat_completions" });

        var act = () => CollectAsync(provider.ChatStreamAsync("astra-chat-only", [], [SampleTool], null, null, null, CancellationToken.None));

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("does not support 'none'");
        handler.RequestUris.Should().OnlyContain(u => u.EndsWith("/chat/completions"));
    }

    [Fact]
    public async Task DeepSeek_NeverSwitchesToResponsesApi()
    {
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.BadRequest, ToolsNeedNoReasoningError), (HttpStatusCode.BadRequest, ReasoningNoneRejectedError));
        var cache = new AIProviderConfigCache();
        cache.SetProvider("deepseek", new Dictionary<string, string?> { ["ApiKey"] = "sk" });
        var provider = new DeepSeekProvider(new HttpClient(handler), cache, Options.Create(new AgentSettings()));

        var act = () => CollectAsync(provider.ChatStreamAsync("deepseek-no-responses", [], [SampleTool], null, null, null, CancellationToken.None));

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.RequestUris.Should().OnlyContain(u => u.EndsWith("/chat/completions"));
    }

    [Fact]
    public async Task ResponsesApi_MapsConversationToolsAndOutputLimitOntoTheRequest()
    {
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, ResponsesTextSse));
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "responses-shape", new Dictionary<string, string?> { [ModelSettings.ApiField] = "responses" });

        AiChatMessage[] messages =
        [
            new("system", "be brief"),
            new("user", "what time is it?"),
            new("assistant", "", [new AiToolCall("call_1", "get_time", "{}")]),
            new("tool", "12:00", ToolCallId: "call_1"),
            new("assistant", "Noon.")
        ];
        await CollectAsync(provider.ChatStreamAsync("responses-shape", messages, [SampleTool], null, null, null, CancellationToken.None));

        handler.RequestUris.Single().Should().Be("https://my-resource.openai.azure.com/openai/v1/responses");
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies.Single());
        var root = body.RootElement;
        root.GetProperty("stream").GetBoolean().Should().BeTrue();
        root.GetProperty("store").GetBoolean().Should().BeFalse();
        root.GetProperty("max_output_tokens").GetInt32().Should().Be(4096);
        root.GetProperty("reasoning").GetProperty("summary").GetString().Should().Be("auto");
        root.TryGetProperty("messages", out _).Should().BeFalse();

        var tool = root.GetProperty("tools")[0];
        tool.GetProperty("type").GetString().Should().Be("function");
        tool.GetProperty("name").GetString().Should().Be("get_time");
        tool.TryGetProperty("function", out _).Should().BeFalse();

        var input = root.GetProperty("input").EnumerateArray().Select(i => i.GetRawText()).ToList();
        input.Should().HaveCount(5);
        input[0].Should().Be("""{"type":"message","role":"system","content":"be brief"}""");
        input[1].Should().Be("""{"type":"message","role":"user","content":"what time is it?"}""");
        input[2].Should().Be("""{"type":"function_call","call_id":"call_1","name":"get_time","arguments":"{}"}""");
        input[3].Should().Be("""{"type":"function_call_output","call_id":"call_1","output":"12:00"}""");
        input[4].Should().Be("""{"type":"message","role":"assistant","content":"Noon."}""");
    }

    [Fact]
    public async Task ResponsesApi_StreamsReasoningAndReturnsToolCallsInOutputOrder()
    {
        var sse =
            """data: {"type":"response.reasoning_summary_part.added","output_index":0,"summary_index":0}""" + "\n\n" +
            """data: {"type":"response.reasoning_summary_text.delta","output_index":0,"summary_index":0,"delta":"Think"}""" + "\n\n" +
            """data: {"type":"response.reasoning_summary_part.added","output_index":0,"summary_index":1}""" + "\n\n" +
            """data: {"type":"response.reasoning_summary_text.delta","output_index":0,"summary_index":1,"delta":"More"}""" + "\n\n" +
            """data: {"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_a","name":"web_search","arguments":""}}""" + "\n\n" +
            """data: {"type":"response.function_call_arguments.delta","output_index":1,"item_id":"fc_1","delta":"{\"q\":"}""" + "\n\n" +
            """data: {"type":"response.output_item.done","output_index":2,"item":{"type":"function_call","id":"fc_2","call_id":"call_b","name":"read_file","arguments":"{\"path\":\"a\"}"}}""" + "\n\n" +
            """data: {"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_a","name":"web_search","arguments":"{\"q\":\"x\"}"}}""" + "\n\n" +
            """data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":5,"output_tokens":9}}}""" + "\n\n";
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, sse));
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "responses-tools", new Dictionary<string, string?> { [ModelSettings.ApiField] = "responses" });

        var chunks = await CollectAsync(provider.ChatStreamAsync("responses-tools", [], [SampleTool], null, null, null, CancellationToken.None));

        string.Concat(chunks.Select(c => c.ReasoningDelta)).Should().Be("Think\n\nMore");
        chunks.Should().NotContain(c => c.ContentDelta != null);
        var final = chunks[^1];
        final.Done.Should().BeTrue();
        final.Usage.Should().Be(new AiTokenUsage(5, 9));
        final.ToolCalls!.Select(t => (t.ToolCallId, t.Name, t.ArgumentsJson)).Should().Equal(
            ("call_a", "web_search", """{"q":"x"}"""),
            ("call_b", "read_file", """{"path":"a"}"""));
    }

    [Fact]
    public async Task ResponsesApi_IncompleteAtOutputCap_FlagsHitOutputLimitAndKeepsBlankArguments()
    {
        var sse =
            """data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","call_id":"call_x","name":"write_file","arguments":""}}""" + "\n\n" +
            """data: {"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":1,"output_tokens":4096}}}""" + "\n\n";
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, sse));
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "responses-cap", new Dictionary<string, string?> { [ModelSettings.ApiField] = "responses" });

        var chunks = await CollectAsync(provider.ChatStreamAsync("responses-cap", [], [SampleTool], null, null, null, CancellationToken.None));

        chunks[^1].HitOutputLimit.Should().BeTrue();
        chunks[^1].ToolCalls!.Single().ArgumentsJson.Should().BeEmpty();
    }

    [Fact]
    public async Task ResponsesApi_FailedEvent_Throws()
    {
        var sse = """data: {"type":"response.failed","response":{"status":"failed","error":{"code":"server_error","message":"boom"}}}""" + "\n\n";
        var handler = new SequencedHttpMessageHandler((HttpStatusCode.OK, sse));
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "responses-failed", new Dictionary<string, string?> { [ModelSettings.ApiField] = "responses" });

        var act = () => CollectAsync(provider.ChatStreamAsync("responses-failed", [], [], null, null, null, CancellationToken.None));

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("boom");
    }

    [Fact]
    public async Task ResponsesApi_ReasoningSummaryRefused_RetriesWithoutItAndRemembers()
    {
        const string summaryRefused =
            """{"error":{"message":"Unsupported parameter: 'reasoning.summary' is not supported with this model.","type":"invalid_request_error","param":"reasoning.summary","code":"unsupported_parameter"}}""";
        var handler = new SequencedHttpMessageHandler(
            (HttpStatusCode.BadRequest, summaryRefused), (HttpStatusCode.OK, ResponsesTextSse), (HttpStatusCode.OK, ResponsesTextSse));
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "responses-no-summary", new Dictionary<string, string?> { [ModelSettings.ApiField] = "responses" });

        var chunks = await CollectAsync(provider.ChatStreamAsync("responses-no-summary", [], [], null, null, null, CancellationToken.None));

        string.Concat(chunks.Select(c => c.ContentDelta)).Should().Be("ok");
        handler.RequestBodies[0].Should().Contain("\"summary\":\"auto\"");
        handler.RequestBodies[1].Should().NotContain("\"reasoning\"");

        await CollectAsync(provider.ChatStreamAsync("responses-no-summary", [], [], null, null, null, CancellationToken.None));
        handler.RequestBodies.Should().HaveCount(3);
        handler.RequestBodies[2].Should().NotContain("\"reasoning\"");
    }

    /// <summary>Replies with each queued response in turn and records every request body and URL.</summary>
    private sealed class SequencedHttpMessageHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<string> RequestBodies { get; } = [];
        public List<string> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestUris.Add(request.RequestUri!.ToString());
            var (status, body) = responses[Math.Min(_next++, responses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task ChatStreamAsync_CallerReasoningEffort_OverridesTheModelsPinnedEffort()
    {
        var handler = new FakeHttpMessageHandler(StopSse);
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "effort-override", new Dictionary<string, string?> { [ModelSettings.ReasoningEffortField] = "low" });

        await foreach (var _ in provider.ChatStreamAsync("effort-override", [], [], null, null, "high", CancellationToken.None)) { }

        handler.LastRequestBody.Should().Contain("\"reasoning_effort\":\"high\"");
    }

    [Fact]
    public async Task ChatStreamAsync_CallerReasoningEffort_IsSentOnTheResponsesApi()
    {
        var handler = new FakeHttpMessageHandler(ResponsesTextSse);
        var provider = CreateAzureProvider(handler, out var cache);
        cache.SetModel("azure", "responses-effort", new Dictionary<string, string?> { [ModelSettings.ApiField] = ModelSettings.ResponsesApi });

        await CollectAsync(provider.ChatStreamAsync("responses-effort", [], [], null, null, "medium", CancellationToken.None));

        handler.LastRequestBody.Should().Contain("\"reasoning\":{\"effort\":\"medium\",\"summary\":\"auto\"}");
    }
}
