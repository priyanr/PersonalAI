using System.Runtime.CompilerServices;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Services.Agent;

namespace PersonalAI.Tests.Agent;

/// <summary>Replays a canned event sequence for any skill run, recording every (skillId, input) call it
/// received — lets AgentExecutionService tests verify skill invocation without a real SkillExecutionService.</summary>
public sealed class FakeSkillExecutionService(Func<Guid, string?, IReadOnlyList<AgentStreamEvent>> responder) : ISkillExecutionService
{
    public List<(Guid SkillId, string? Input)> Calls { get; } = [];

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(Guid skillId, string? input, [EnumeratorCancellation] CancellationToken ct)
    {
        Calls.Add((skillId, input));
        foreach (var evt in responder(skillId, input))
        {
            await Task.Yield();
            yield return evt;
        }
    }
}
