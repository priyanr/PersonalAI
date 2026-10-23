namespace PersonalAI.Core.Models.Entities;

/// <summary>One observation for one entity (e.g. a connection, or one item within it) at one instant.
/// ValuesJson is a flat JSON object. Ts is a UTC DateTime rather than DateTimeOffset so SQLite can
/// compare and order it.</summary>
public class DataPoint
{
    public long Id { get; set; }
    public Guid DatasetId { get; set; }
    public required string EntityKey { get; set; }
    public DateTime Ts { get; set; }
    public required string ValuesJson { get; set; }
}
