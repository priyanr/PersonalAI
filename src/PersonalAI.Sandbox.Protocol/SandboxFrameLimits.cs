namespace PersonalAI.Sandbox.Protocol;

/// <summary>The one formula both ends of the WebSocket must agree on for WebSocketFrameTransport's inbound
/// frame-size cap. Lives here — not duplicated in SandboxHostRegistry (API) and BrokerConnection (broker)
/// — specifically because those two are separate processes built independently: a future edit to the
/// margin/floor logic applied to only one copy would silently reintroduce the bug this exists to prevent
/// (an oversized frame doesn't just fail its own request — WebSocketFrameTransport.ReceiveAsync throws,
/// which SandboxFramePump's reader loop turns into faulting every pending request and killing the whole
/// connection). Takes primitive values rather than either side's own Settings type so this project — kept
/// deliberately free of any dependency beyond the BCL — never needs one either.</summary>
public static class SandboxFrameLimits
{
    public static int MaxFrameBytesFor(long maxZipBytes, long maxDownloadBytes)
    {
        var payloadCeiling = Math.Max(maxZipBytes, maxDownloadBytes);
        var withMargin = (long)(payloadCeiling * 1.5) + 1024 * 1024;
        return (int)Math.Clamp(withMargin, 16 * 1024 * 1024, int.MaxValue);
    }
}
