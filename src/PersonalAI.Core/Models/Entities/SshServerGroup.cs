namespace PersonalAI.Core.Models.Entities;

/// <summary>A named set of monitored servers. Dashboard agents reference groups (not individual servers)
/// so servers can be added to a group without editing every agent.</summary>
public class SshServerGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<SshServer> Servers { get; set; } = [];
}
