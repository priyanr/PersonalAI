using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;

namespace PersonalAI.Sandbox.Protocol;

/// <summary>IFrameTransport over a live System.Net.WebSockets.WebSocket. WebSocket is a BCL type (not
/// ASP.NET-specific), so this needs no package reference and works identically for the API's server-side
/// accepted socket and the broker's client-side ClientWebSocket.</summary>
public sealed class WebSocketFrameTransport(WebSocket socket, int maxFrameBytes = 16 * 1024 * 1024) : IFrameTransport
{
    public async Task SendAsync(SandboxFrame frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, SandboxJson.Options);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    public async Task<SandboxFrame?> ReceiveAsync(CancellationToken ct)
    {
        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(8192);
        try
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                ms.Write(buffer, 0, result.Count);
                if (ms.Length > maxFrameBytes)
                    throw new InvalidOperationException($"Sandbox frame exceeded the {maxFrameBytes}-byte limit before EndOfMessage.");
            } while (!result.EndOfMessage);

            if (ms.Length == 0)
                return null;

            ms.Position = 0;
            return JsonSerializer.Deserialize<SandboxFrame>(ms, SandboxJson.Options);
        }
        finally
        {
            pool.Return(buffer);
        }
    }
}
