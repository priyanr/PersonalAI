using System.Runtime.CompilerServices;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Settings;
using PersonalAI.Core.Services.AI;
using PersonalAI.Core.Services.Secrets;
using PersonalAI.Core.Services.Skills;
using PersonalAI.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace PersonalAI.Tests.Agent;

/// <summary>The provider every skill runs on in a test: answers each skill run with <paramref name="reply"/> as
/// plain text and records the run's input, so chat and agent tests can use a real SkillExecutionService.</summary>
public sealed class FakeSkillProvider(string reply = "") : IAIProvider
{
    public string Key => "ollama";
    public string DisplayName => "Fake skill provider";
    public IReadOnlyList<ProviderConfigField> ConfigFields => [];

    /// <summary>The user message of each skill run, which is the input the caller passed.</summary>
    public List<string> Inputs { get; } = [];

    public SkillExecutionService CreateService(AppDbContext db)
    {
        return new SkillExecutionService(
            new SkillsRepository(db), new AIProviderRegistry([this]), new ToolRegistry([]), new SecretsRepository(db),
            Options.Create(new AgentSettings { MaxIterations = 25 }), Mock.Of<ISandboxHostClient>(),
            NullLogger<SkillExecutionService>.Instance);
    }

    public async IAsyncEnumerable<AiChatChunk> ChatStreamAsync(
        string model, IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AiToolDefinition> tools,
        int? numCtx, bool? think, string? reasoningEffort, [EnumeratorCancellation] CancellationToken ct)
    {
        Inputs.Add(messages.Last(m => m.Role == "user").Content!);
        await Task.Yield();
        yield return new AiChatChunk(reply, null, true);
    }

    public Task<IReadOnlyList<AiModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        throw new NotSupportedException("Not used by skill runs.");
    }

    public IAsyncEnumerable<AiPullProgress> PullModelStreamAsync(string modelName, CancellationToken ct)
    {
        throw new NotSupportedException("Not used by skill runs.");
    }
}
