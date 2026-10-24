using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;

namespace PersonalAI.Core.Services.Monitoring.Collectors;

/// <summary>The one dataset the platform writes itself: whether each connection could be reached on the last pass. A
/// server that is down, a database that refuses logins or an API that times out are data too, so an alert rule can
/// fire on <c>available = false</c> whatever the tool. Only a failure to connect counts: a slow or failing request on a
/// connection that did answer is a problem with that job, not an outage.</summary>
public static class DataSourceStatus
{
    public const string DatasetName = "data_source_status";

    public const string SchemaHint =
        "One entity per connection (server, database...), written by the platform on every collection pass that ran a job on it. " +
        "available (bool: false when the connection could not be made), tool (which data tool), target (the connection name); " +
        "when available is false: error.";

    public static readonly DatasetDefinition Definition = new(DatasetName, DatasetKind.Timeseries, SchemaHint);

    public static NewDataPoint Point(DataConnection connection, string tool, bool available, string? error, DateTime ts)
    {
        return new(DatasetName, DatasetKind.Timeseries, connection.Id.ToString(), ts, available
            ? JsonSerializer.Serialize(new { target = connection.Name, tool, available })
            : JsonSerializer.Serialize(new { target = connection.Name, tool, available, error }));
    }
}
