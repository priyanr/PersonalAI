using PersonalAI.Core.Interfaces;

namespace PersonalAI.Api.BackgroundServices;

/// <summary>On shutdown, stops every background chat turn and gives each the host's shutdown window to
/// record its interruption, so a restart mid-turn leaves "Turn interrupted: the server shut down" in the
/// transcript instead of a tool call that silently never got a result.</summary>
public sealed class ChatTurnShutdownService(IChatTurnRunner turns) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => turns.StopAllAsync(cancellationToken);
}
