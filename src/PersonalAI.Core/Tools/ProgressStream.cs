using System.Threading.Channels;
using PersonalAI.Core.Services.Agent;

namespace PersonalAI.Core.Tools;

/// <summary>Runs work in the background while the messages it reports (a tool's "booting the sandbox...", a model's
/// streamed text) reach the caller as events. The agent loops are iterators, which can't yield from inside the work's
/// own callback or catch block.</summary>
public static class ProgressStream
{
    /// <summary>Read <c>Events</c> to the end, then await <c>Result</c>.</summary>
    public static (IAsyncEnumerable<AgentStreamEvent> Events, Task<T> Result) Run<T>(
        Func<Action<string>, Task<T>> work, Func<string, AgentStreamEvent> toEvent, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>();
        var result = Task.Run(async () =>
        {
            try
            {
                return await work(message => channel.Writer.TryWrite(toEvent(message)));
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);
        return (channel.Reader.ReadAllAsync(ct), result);
    }
}
