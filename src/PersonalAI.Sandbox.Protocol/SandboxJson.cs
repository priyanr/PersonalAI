using System.Text.Json;

namespace PersonalAI.Sandbox.Protocol;

/// <summary>The one JsonSerializerOptions instance both ends of the WebSocket must agree on — same
/// options used to serialize a SandboxFrame must be used to deserialize it, or the "$type" discriminator
/// and camelCase property names on the wire won't line up.</summary>
public static class SandboxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
