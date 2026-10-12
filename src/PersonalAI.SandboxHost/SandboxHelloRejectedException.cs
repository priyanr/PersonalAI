namespace PersonalAI.SandboxHost;

/// <summary>Thrown when the API answers HostHello with HostHelloAck(Accepted: false) — a config error
/// (wrong host key, Sandbox:Enabled off, ...), not a transient network blip. BrokerConnection backs off
/// harder in this case than for an ordinary dropped connection.</summary>
public sealed class SandboxHelloRejectedException(string? reason)
    : Exception($"The API rejected this broker's HostHello: {reason ?? "no reason given"}");
