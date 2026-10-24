using FluentAssertions;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.Agent;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Secrets;
using PersonalAI.Core.Services.Skills;
using PersonalAI.Core.Tools;
using PersonalAI.Tests.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace PersonalAI.Tests.Services;

public class SkillExecutionServiceTests
{
    private static AppDbContext NewContext() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SkillExecutionService CreateService(
        AppDbContext db, FakeAIProvider provider, ToolRegistry tools,
        AgentSettings? agentSettings = null, ISandboxHostClient? sandboxHostClient = null) => new(
        new SkillsRepository(db),
        new AIProviderRegistry([(IAIProvider)provider]),
        tools,
        new SecretsRepository(db),
        Options.Create(agentSettings ?? new AgentSettings { MaxIterations = 25 }),
        sandboxHostClient ?? Mock.Of<ISandboxHostClient>(),
        Mock.Of<ILogger<SkillExecutionService>>());

    [Fact]
    public async Task RunAsync_PlainTextReply_EndsWithDone()
    {
        await using var db = NewContext();
        var skill = await TestSeed.SkillAsync(db, "s", null, "You are helpful.", "ollama", "llama3.1:8b", null, [], [], CancellationToken.None);

        var provider = new FakeAIProvider([new AiChatChunk("Hi there", null, true)]);
        var service = CreateService(db, provider, new ToolRegistry([]));

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(skill.Id, "hello", CancellationToken.None))
            events.Add(e);

        events.OfType<TextDeltaEvent>().Select(e => e.Text).Should().Equal("Hi there");
        events.Should().ContainSingle(e => e is DoneEvent);
    }

    [Fact]
    public async Task RunAsync_ToolCallWithinAllowlist_ExecutesAndLoopsToFinalAnswer()
    {
        await using var db = NewContext();
        var skill = await TestSeed.SkillAsync(db, "s", null, "prompt", "ollama", "llama3.1:8b", null, ["allowed_tool"], [], CancellationToken.None);

        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "allowed_tool", "{}")], true)],
            [new AiChatChunk("Done!", null, true)]);
        var tools = new ToolRegistry([new FakeTool("allowed_tool", (_, _) => new ToolCompleted("tool result"))]);
        var service = CreateService(db, provider, tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(skill.Id, null, CancellationToken.None))
            events.Add(e);

        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.ResultContent == "tool result" && !e.IsError);
        events.Should().ContainSingle(e => e is DoneEvent);
        provider.RecordedRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_ToolNotInAllowlist_RejectsWithoutExecutingIt()
    {
        await using var db = NewContext();
        // Skill only allows "allowed_tool" — the model tries a different, registered tool.
        var skill = await TestSeed.SkillAsync(db, "s", null, "prompt", "ollama", "llama3.1:8b", null, ["allowed_tool"], [], CancellationToken.None);

        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "other_tool", "{}")], true)],
            [new AiChatChunk("Understood", null, true)]);

        var executed = false;
        var tools = new ToolRegistry([
            new FakeTool("allowed_tool", (_, _) => new ToolCompleted("ok")),
            new FakeTool("other_tool", (_, _) => { executed = true; return new ToolCompleted("should not run"); })
        ]);
        var service = CreateService(db, provider, tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(skill.Id, null, CancellationToken.None))
            events.Add(e);

        executed.Should().BeFalse();
        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError && e.ResultContent!.Contains("not enabled"));
    }

    [Fact]
    public async Task RunAsync_ToolRequiresApproval_RejectsInsteadOfPausing()
    {
        await using var db = NewContext();
        var skill = await TestSeed.SkillAsync(db, "s", null, "prompt", "ollama", "llama3.1:8b", null, ["gated_tool"], [], CancellationToken.None);

        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "gated_tool", "{}")], true)],
            [new AiChatChunk("Cannot do that unattended", null, true)]);
        var tools = new ToolRegistry([
            new FakeTool("gated_tool", (_, _) => new ToolApprovalRequired("outside workspace", "/outside/path"))
        ]);
        var service = CreateService(db, provider, tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(skill.Id, null, CancellationToken.None))
            events.Add(e);

        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError && e.ResultContent!.Contains("unattended"));
        events.Should().NotContain(e => e is ApprovalRequiredEvent);
        provider.RecordedRequests.Should().HaveCount(2); // loop continued instead of halting
    }

    [Fact]
    public async Task RunAsync_SecretPlaceholder_SubstitutedIntoToolArgumentsButNeverSentToProvider()
    {
        await using var db = NewContext();
        var skill = await TestSeed.SkillAsync(db, 
            "s", null, "prompt", "ollama", "llama3.1:8b", null, ["http_tool"], ["API_KEY"], CancellationToken.None);

        db.Secrets.Add(new Secret { Key = "API_KEY", Value = "super-secret-value" });
        await db.SaveChangesAsync();

        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "http_tool", """{"header":"Bearer {{secret.API_KEY}}"}""")], true)],
            [new AiChatChunk("Done", null, true)]);

        string? receivedHeader = null;
        var tools = new ToolRegistry([
            new FakeTool("http_tool", (args, _) =>
            {
                receivedHeader = args.GetProperty("header").GetString();
                return new ToolCompleted("ok");
            })
        ]);
        var service = CreateService(db, provider, tools);

        await foreach (var _ in service.RunAsync(skill.Id, null, CancellationToken.None)) { }

        receivedHeader.Should().Be("Bearer super-secret-value");
        // The provider only ever sees the placeholder token in the tool call it issued, never the resolved value.
        provider.RecordedRequests.SelectMany(r => r).Should().NotContain(m => m.Content != null && m.Content.Contains("super-secret-value"));
    }

    [Fact]
    public async Task RunAsync_SecretNotDeclaredBySkill_PlaceholderLeftUnresolved()
    {
        await using var db = NewContext();
        // Skill does not declare "API_KEY" in its SecretKeys, even though it is stored.
        var skill = await TestSeed.SkillAsync(db, "s", null, "prompt", "ollama", "llama3.1:8b", null, ["http_tool"], [], CancellationToken.None);

        db.Secrets.Add(new Secret { Key = "API_KEY", Value = "super-secret-value" });
        await db.SaveChangesAsync();

        var provider = new FakeAIProvider(
            [new AiChatChunk("", [new AiToolCall("call1", "http_tool", """{"header":"Bearer {{secret.API_KEY}}"}""")], true)],
            [new AiChatChunk("Done", null, true)]);

        string? receivedHeader = null;
        var tools = new ToolRegistry([
            new FakeTool("http_tool", (args, _) =>
            {
                receivedHeader = args.GetProperty("header").GetString();
                return new ToolCompleted("ok");
            })
        ]);
        var service = CreateService(db, provider, tools);

        await foreach (var _ in service.RunAsync(skill.Id, null, CancellationToken.None)) { }

        receivedHeader.Should().Be("Bearer {{secret.API_KEY}}");
    }

    [Fact]
    public async Task RunAsync_UnknownSkillId_Throws()
    {
        await using var db = NewContext();
        var provider = new FakeAIProvider([new AiChatChunk("Hi", null, true)]);
        var service = CreateService(db, provider, new ToolRegistry([]));

        var act = async () =>
        {
            await foreach (var _ in service.RunAsync(Guid.NewGuid(), null, CancellationToken.None)) { }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RunAsync_ExceedsMaxIterations_EmitsErrorEvent()
    {
        await using var db = NewContext();
        var skill = await TestSeed.SkillAsync(db, "s", null, "prompt", "ollama", "llama3.1:8b", null, ["loop_tool"], [], CancellationToken.None);

        var responses = Enumerable.Range(0, 3)
            .Select(_ => (IReadOnlyList<AiChatChunk>)[new AiChatChunk("", [new AiToolCall("call1", "loop_tool", "{}")], true)])
            .ToArray();
        var provider = new FakeAIProvider(responses);
        var tools = new ToolRegistry([new FakeTool("loop_tool", (_, _) => new ToolCompleted("still going"))]);
        var service = CreateService(db, provider, tools, agentSettings: new AgentSettings { MaxIterations = 3 });

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(skill.Id, null, CancellationToken.None))
            events.Add(e);

        events.Should().ContainSingle(e => e is ErrorEvent);
    }

    [Fact]
    public async Task RunAsync_EmptyReplyAtOutputLimit_EndsWithErrorInsteadOfDone()
    {
        await using var db = NewContext();
        var skill = await TestSeed.SkillAsync(db, "s", null, "prompt", "ollama", "llama3.1:8b", null, [], [], CancellationToken.None);

        var provider = new FakeAIProvider([
            new AiChatChunk(null, null, false, ReasoningDelta: "thinking"),
            new AiChatChunk(null, null, true, HitOutputLimit: true)
        ]);
        var service = CreateService(db, provider, new ToolRegistry([]));

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(skill.Id, "go", CancellationToken.None))
            events.Add(e);

        events.Should().NotContain(e => e is DoneEvent);
        events.OfType<ErrorEvent>().Single().Message.Should().Contain("thinking").And.NotContain("thinking off");
        provider.RecordedThink.Should().Equal([(bool?)null], "a skill has no thinking preference of its own");
    }

    [Fact]
    public async Task RunAsync_TruncatedToolCall_IsNotExecuted()
    {
        await using var db = NewContext();
        var skill = await TestSeed.SkillAsync(db, "s", null, "prompt", "ollama", "llama3.1:8b", null, ["allowed_tool"], [], CancellationToken.None);

        var ran = false;
        var provider = new FakeAIProvider(
            [new AiChatChunk(null, [new AiToolCall("c1", "allowed_tool", "{\"content\":\"parti")], true, HitOutputLimit: true)],
            [new AiChatChunk("Done", null, true)]);
        var tools = new ToolRegistry([new FakeTool("allowed_tool", (_, _) => { ran = true; return new ToolCompleted("ran"); })]);
        var service = CreateService(db, provider, tools);

        var events = new List<AgentStreamEvent>();
        await foreach (var e in service.RunAsync(skill.Id, "go", CancellationToken.None))
            events.Add(e);

        ran.Should().BeFalse();
        events.OfType<ToolResultEvent>().Should().ContainSingle(e => e.IsError && e.ResultContent!.Contains("output-token limit"));
        events.Should().ContainSingle(e => e is DoneEvent);
    }
}
