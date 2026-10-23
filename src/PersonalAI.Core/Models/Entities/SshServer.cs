namespace PersonalAI.Core.Models.Entities;

/// <summary>A machine reachable over SSH that collector tools run read-only commands against. The
/// credential is never stored here: SecretRef names an entry in the secrets vault whose value is either
/// a PEM private key or a password.</summary>
public class SshServer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public SshServerGroup? Group { get; set; }

    public required string Name { get; set; }
    public required string Address { get; set; }
    public int Port { get; set; } = 22;
    public required string Username { get; set; }

    /// <summary>Key of the Secret holding this server's private key (PEM) or password.</summary>
    public required string SecretRef { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>SHA256 host-key fingerprint pinned on first successful connection (trust on first use).
    /// A later mismatch fails the connection until the pin is cleared.</summary>
    public string? HostKeyFingerprint { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Last connection failure message; null after a successful connection.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
