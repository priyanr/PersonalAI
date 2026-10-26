namespace PersonalAI.Core.Models.Entities;

public enum DatasetKind
{
    /// <summary>Numeric samples over time (a metric, a count…).</summary>
    Timeseries = 0,
    /// <summary>The latest state per entity matters; older points are history only (an inventory, a status).</summary>
    Snapshot = 1,
    /// <summary>Discrete occurrences (alert-worthy events).</summary>
    Event = 2
}

/// <summary>A named collection of data points owned by one agent. Deliberately generic: nothing here is
/// specific to any one data source, so other collectors can write their own datasets.</summary>
public class Dataset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public required string Name { get; set; }
    public DatasetKind Kind { get; set; }

    /// <summary>Free-form description of the value fields, shown to the model by data_query.</summary>
    public string? SchemaHint { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
