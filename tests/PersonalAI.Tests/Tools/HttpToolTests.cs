using System.Net;
using System.Text.Json;
using FluentAssertions;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Tools;
using PersonalAI.Tests.Services;
using Microsoft.Extensions.Options;
using Moq;

namespace PersonalAI.Tests.Tools;

public class HttpToolTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private static (HttpTool Tool, FakeHttpMessageHandler Handler) NewTool(
        HttpToolSettings? settings = null, string responseBody = "{\"ok\":true}", HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new FakeHttpMessageHandler(responseBody, statusCode);
        var client = new HttpClient(handler);
        var factory = Mock.Of<IHttpClientFactory>(f => f.CreateClient(HttpTool.HttpClientName) == client);
        var tool = new HttpTool(factory, Options.Create(settings ?? new HttpToolSettings { AllowedUrls = ["https://api.example.com/*"] }));
        return (tool, handler);
    }

    [Fact]
    public async Task ExecuteAsync_MissingUrl_Throws()
    {
        var (tool, _) = NewTool();
        var act = async () => await tool.ExecuteAsync(Args(new { }), new ToolExecutionContext(false), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_UrlNotInAllowList_ReturnsErrorWithoutSendingRequest()
    {
        var (tool, handler) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { url = "https://evil.example.org/data" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("not permitted");
        handler.LastRequestUri.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_UrlMatchesWildcardAllowList_SendsRequest()
    {
        var (tool, handler) = NewTool(responseBody: "{\"hello\":\"world\"}");
        var result = await tool.ExecuteAsync(Args(new { url = "https://api.example.com/v1/things" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeFalse();
        handler.LastRequestUri!.ToString().Should().Be("https://api.example.com/v1/things");

        using var json = JsonDocument.Parse(completed.Content);
        json.RootElement.GetProperty("statusCode").GetInt32().Should().Be(200);
        json.RootElement.GetProperty("body").GetString().Should().Be("{\"hello\":\"world\"}");
    }

    [Fact]
    public async Task ExecuteAsync_NonSuccessStatus_StillReturnsCompletedWithStatusCodeInBody()
    {
        var (tool, _) = NewTool(statusCode: HttpStatusCode.NotFound);
        var result = await tool.ExecuteAsync(Args(new { url = "https://api.example.com/missing" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeFalse();
        using var json = JsonDocument.Parse(completed.Content);
        json.RootElement.GetProperty("statusCode").GetInt32().Should().Be(404);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedMethod_ReturnsErrorWithoutSendingRequest()
    {
        var (tool, handler) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { url = "https://api.example.com/x", method = "TRACE" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        handler.LastRequestUri.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_BodyWithoutContentTypeHeader_DefaultsToJson()
    {
        var (tool, handler) = NewTool();
        await tool.ExecuteAsync(
            Args(new { url = "https://api.example.com/x", method = "POST", body = "{\"a\":1}" }),
            new ToolExecutionContext(false), CancellationToken.None);

        handler.LastRequestBody.Should().Be("{\"a\":1}");
    }

    [Fact]
    public async Task ExecuteAsync_ResponseBodyExceedsLimit_IsTruncated()
    {
        var longBody = new string('x', 100);
        var (tool, _) = NewTool(new HttpToolSettings { AllowedUrls = ["https://api.example.com/*"], MaxResponseBodyChars = 10 }, responseBody: longBody);
        var result = await tool.ExecuteAsync(Args(new { url = "https://api.example.com/big" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        using var json = JsonDocument.Parse(completed.Content);
        json.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("body").GetString().Should().HaveLength(10);
    }
}
