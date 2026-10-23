using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Services.Monitoring.Collectors;
using PersonalAI.Core.Services.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalAI.Core.Tools;

/// <summary>Shared description and schema for the job tools, so the model sees the same contract everywhere.</summary>
internal static class CollectorToolText
{
    public const string JobExplanation =
        "A collection job sends a request to one of this agent's data tools (for ssh: a command on a server) on an interval with NO model involved, parses the answer into " +
        "rows, and appends them to a dataset that dashboards and alert rules read. You design it once; the platform runs it.\n\n" +
        "Fields: name; description; tool (which data tool runs it, one of your tools; may be left out when you have only one); request (what the tool " +
        "understands, one read-only request, ideally within the tool's allowed list; for ssh a command, optionally piped through grep/head/tail/sort/uniq/cut/tr/wc); " +
        "intervalMinutes (1-1440, default 5); parser; dataset; targets (optional connection names, e.g. server or database names; default all of " +
        "this agent's connections for that tool).\n\n" +
        "parser.type is one of:\n" +
        "- table: whitespace-separated columns. {type:'table', skipLines:1, columns:[{name,type}], lastColumnTakesRest:true}. type is string (default), number or bool; a trailing % and thousands commas are stripped from numbers. Example for `df -P`: columns fs,size,used,avail,pct,mount, skipLines 1, lastColumnTakesRest true.\n" +
        "- json: {type:'json', path:'items', fields:{name:{path:'metadata.name'}, restarts:{path:'status.containerStatuses.0.restartCount', type:'number'}}}. path is dotted (numeric segments index arrays) and points at an array (a row per element) or an object (one row). Use `-o json` / `--format '{{json .}}'` style output.\n" +
        "- kv: key=value or key: value lines into one row; columns optionally type them.\n" +
        "- regex: {type:'regex', pattern:'^(?<name>\\\\S+)\\\\s+(?<cpu>[\\\\d.]+)%', columns:[{name:'cpu',type:'number'}]}: named groups per line.\n\n" +
        "dataset: {name, kind, keyFields}. name is lowercase letters, digits, '_' or '-'. kind: timeseries (numeric samples over time; default), snapshot (current state of things, e.g. pods) or event. keyFields (optional) are parsed fields that identify one entity within a connection (e.g. ['namespace','name'] for pods, ['mount'] for disks); without them each connection is one entity. " +
        "The platform adds two fields to every row: target (the connection name) and entity (the key fields joined by '/'); don't use those names.\n\n" +
        "A request that is not on the tool's allowed list is not refused outright: the job is saved waiting for the USER to approve that " +
        "exact request (it cannot be dry-run first, so be careful to get it right), and it runs only after they do. Anything the tool " +
        "refuses outright (for ssh: chaining, redirection, substitution) is never run. Prefer allowed requests; ask for approval only when nothing allowed gives the data.\n\n" +
        "Example (ssh): {name:'K8s pods', tool:'ssh', request:'kubectl get pods -A -o json', intervalMinutes:5, " +
        "parser:{type:'json', path:'items', fields:{name:{path:'metadata.name'}, namespace:{path:'metadata.namespace'}, phase:{path:'status.phase'}, restarts:{path:'status.containerStatuses.0.restartCount', type:'number'}}}, " +
        "dataset:{name:'k8s_pods', kind:'snapshot', keyFields:['namespace','name']}}";

    public const string JobSchema = """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Unique job name; saving an existing name updates that job." },
            "description": { "type": "string" },
            "tool": { "type": "string", "description": "The data tool that runs the request. May be left out when the agent has only one." },
            "request": { "type": "string", "description": "One read-only request for the tool (for ssh a command, for a database tool a query). Outside the tool's allowed list it needs the user's approval." },
            "intervalMinutes": { "type": "integer", "description": "1-1440. Default 5." },
            "parser": {
              "type": "object",
              "properties": {
                "type": { "type": "string", "enum": ["table", "json", "kv", "regex"] },
                "skipLines": { "type": "integer" },
                "columns": { "type": "array", "items": { "type": "object", "properties": { "name": { "type": "string" }, "type": { "type": "string", "enum": ["string", "number", "bool"] } } } },
                "lastColumnTakesRest": { "type": "boolean" },
                "path": { "type": "string" },
                "fields": { "type": "object", "additionalProperties": { "type": "object", "properties": { "path": { "type": "string" }, "type": { "type": "string", "enum": ["string", "number", "bool"] } } } },
                "pattern": { "type": "string" }
              },
              "required": ["type"]
            },
            "dataset": {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "kind": { "type": "string", "enum": ["timeseries", "snapshot", "event"] },
                "keyFields": { "type": "array", "items": { "type": "string" } }
              },
              "required": ["name"]
            },
            "targets": { "type": "array", "items": { "type": "string" }, "description": "Connection names (e.g. servers, databases) to run on. Omit for all in this agent's scope for the tool." }
          },
          "required": ["name", "request", "parser", "dataset"]
        }
        """;

    public static (JobInput? Input, ToolCompleted? Error) ParseInput(JsonElement arguments)
    {
        try
        {
            var input = JsonSerializer.Deserialize<JobInput>(arguments.GetRawText(), CollectorSpecs.Json);
            return input is null ? (null, new ToolCompleted("Arguments are required.", IsError: true)) : (input, null);
        }
        catch (JsonException ex)
        {
            return (null, new ToolCompleted($"Arguments are not valid: {ex.Message}", IsError: true));
        }
    }

    public static string Sample(IEnumerable<JsonObject> rows, int max = 3) =>
        string.Join("\n", rows.Take(max).Select(r => "  " + r.ToJsonString()));

    public static string Describe(CollectionJob j) =>
        $"{j.Name} [{j.Status}, {j.Source}{(j.OutsideAllowlist ? ", outside the allowed list" : "")}]: every {j.IntervalMinutes} min: {Short(j.Request)}";

    private static string Short(string s) => s.Length <= 160 ? s.ReplaceLineEndings(" ") : s[..157].ReplaceLineEndings(" ") + "...";
}

public sealed class SshTool(CollectionRunner runner) : ITool
{
    public bool RequiresAgentRun => true;

    public string Name => SshGuardProvider.ToolName;

    public string Description =>
        "Runs ONE read-only command on one of this agent's servers over SSH and returns its output, so you can explore what " +
        "is available (disks, services, docker, kubectl output, files under /proc) before designing a collection job. Only an " +
        "allowlist of read-only commands is accepted; chaining (; & ||), redirection and command substitution are always refused, " +
        "and a command that is not on the list is not run (the refusal lists what is allowed; if you need it, put it in a " +
        "collection job, which the user can approve as written). A command may be piped through filters " +
        "(grep head tail sort uniq cut tr wc) that read only the previous command's output. 30-second limit. Omit 'target' to " +
        "use the agent's first server.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "One allowlisted read-only command, optionally piped through filters." },
            "target": { "type": "string", "description": "Server name. Omit for the first server." }
          },
          "required": ["command"]
        }
        """;

    // Settings > Tools > SSH: the command allowlist, and the servers (a custom component) the agents connect to.
    public IReadOnlyList<ToolSettingField> SettingsFields =>
    [
        new(SshGuardProvider.AllowedCommandsField, "Allowed commands", ToolSettingFieldType.TextArea, Required: false,
            DefaultValue: SshCommandGuard.DefaultAllowlistText,
            Help: "One command per line; a model may only run commands that start with one of these (plus stdin filters after a pipe). " +
                  "An entry ending in '/' limits that command's path arguments, e.g. 'cat /proc/'. Leave blank for the defaults. " +
                  "Only add commands that cannot change anything or run other programs: awk, sed, find, xargs, tee and shells are left out on purpose."),
        new("Servers", "Servers", ToolSettingFieldType.Custom, Required: false, CustomComponentKey: "sshServers")
    ];

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();
        if (arguments.GetStringArg("command") is not { Length: > 0 } command)
            return new ToolCompleted("'command' is required.", IsError: true);

        var result = await runner.RunCommandAsync(agentId, SshGuardProvider.ToolName, arguments.GetStringArg("target"), command, ct);
        if (result.Error is not null && result.Output.Length == 0) return new ToolCompleted(result.Error, IsError: true);

        var text = result.Output.Length == 0 ? "(no output)" : result.Output;
        if (result.Error is not null) text += $"\n[{result.Error}]";
        return new ToolCompleted(MonitoringToolSupport.Truncate(text), IsError: !result.Success);
    }
}

public sealed class CollectorTestTool(CollectionRunner runner) : ITool
{
    public bool RequiresAgentRun => true;

    public string Name => "collector_test";

    public string Description =>
        "Dry-runs a proposed collection job: validates it, sends the request once to one connection, parses the answer and shows " +
        "the first rows. Nothing is saved. Use it to get the request and parser right before collector_save; if no rows come " +
        "back it shows the start of the raw output so you can fix the parser. The test runs on the first connection in " +
        "'targets', or the agent's first connection for the tool.\n\n" + CollectorToolText.JobExplanation;

    public string ParametersSchemaJson => CollectorToolText.JobSchema;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();
        var (input, error) = CollectorToolText.ParseInput(arguments);
        if (error is not null) return error;

        var outcome = await runner.TestAsync(agentId, input!, input!.Targets?.FirstOrDefault(), ct);
        return new ToolCompleted(Format(outcome), IsError: !outcome.Success);
    }

    internal static string Format(TestOutcome o)
    {
        if (o.ValidationErrors.Count > 0) return "Invalid job:\n- " + string.Join("\n- ", o.ValidationErrors);
        if (o.NeedsApproval)
            return "The request is not on the tool's allowed list, so it was not run. Saving it with collector_save asks the user to approve " +
                   "this exact request; it runs only after they do. Its results are not available until then.";
        if (!o.Success) return $"The test on '{o.Target}' failed: {o.Error}";

        var sb = new StringBuilder($"OK on '{o.Target}': {o.RowCount} row(s) parsed. First rows:\n{CollectorToolText.Sample(o.SampleRows)}");
        if (o.Diagnostic is not null) sb.Append($"\nNote: {o.Diagnostic}");
        return sb.ToString();
    }
}

public sealed class CollectorSaveTool(IServiceScopeFactory scopes, CollectionRunner runner) : ITool
{
    public bool RequiresAgentRun => true;

    public string Name => "collector_save";

    public string Description =>
        "Saves a collection job (creating it, or updating the job with that name). The job is validated and dry-run on a " +
        "connection first: nothing is saved if the request is refused or produces no rows, and you get the reason. Jobs you " +
        "create may need the USER's approval before they run: the user sees the exact request, and until they approve, no data " +
        "is collected. You can still build dashboards and alert rules on the dataset in the meantime. Changing an existing " +
        "job's request, parser, dataset or connections sends it back for approval. You can only change jobs you created.\n\n" +
        CollectorToolText.JobExplanation;

    public string ParametersSchemaJson => CollectorToolText.JobSchema;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();
        var (input, error) = CollectorToolText.ParseInput(arguments);
        if (error is not null) return error;

        // Prove it works on a real connection before anything is saved.
        // A request outside the allowed list can't be tried first; it is saved for the user to approve as written.
        var test = await runner.TestAsync(agentId, input!, input!.Targets?.FirstOrDefault(), ct);
        if (!test.Success && !test.NeedsApproval) return new ToolCompleted("Not saved. " + CollectorTestTool.Format(test), IsError: true);

        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<CollectionJobService>()
            .UpsertByNameAsync(agentId, input, JobSources.Llm, ct);
        if (!result.IsValid) return new ToolCompleted("Not saved:\n- " + string.Join("\n- ", result.Errors), IsError: true);

        var job = result.Job!;
        var status = job.Status == JobStatus.PendingApproval
            ? "It is waiting for the user's approval, so no data is collected yet. Tell the user (in your final reply) which job needs approving" +
              (job.OutsideAllowlist ? ", and that its request is not on the tool's allowed list so they must read it carefully." : ".")
            : $"It is active and will run every {job.IntervalMinutes} minute(s).";
        return new ToolCompleted($"{(result.Created ? "Created" : "Updated")} job '{job.Name}'. {status}" + (test.NeedsApproval ? "" : $"\n{CollectorTestTool.Format(test)}"));
    }
}

public sealed class CollectorListTool(IServiceScopeFactory scopes) : ITool
{
    public bool RequiresAgentRun => true;

    public string Name => "collector_list";

    public string Description =>
        "Lists this agent's collection jobs: name, status (pending_approval, active, disabled), who created it, interval, " +
        "request, the dataset it writes, and the result of its last run. Call it before creating jobs so you update rather " +
        "than duplicate, and to see whether a job is failing.";

    public string ParametersSchemaJson => """{ "type": "object", "properties": {} }""";

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();

        await using var scope = scopes.CreateAsyncScope();
        var jobs = await scope.ServiceProvider.GetRequiredService<Interfaces.ICollectionJobsRepository>().GetAllAsync(agentId, ct);
        if (jobs.Count == 0) return new ToolCompleted("No jobs yet. Create jobs with collector_save.");

        var sb = new StringBuilder();
        foreach (var j in jobs)
        {
            sb.AppendLine($"- {CollectorToolText.Describe(j)}");
            var (dataset, _) = CollectorSpecs.Parse<DatasetSpec>(j.DatasetJson, "dataset");
            if (dataset?.Name is not null) sb.AppendLine($"    dataset: {dataset.Name} ({dataset.Kind ?? "timeseries"}){(dataset.KeyFields is { Count: > 0 } k ? ", key " + string.Join("/", k) : "")}");
            sb.AppendLine(j.LastRunAt is null
                ? "    last run: never"
                : $"    last run: {j.LastRunAt:u}, {j.LastRowCount} row(s){(j.LastError is null ? "" : $", ERROR: {j.LastError}")}");
        }
        return new ToolCompleted(MonitoringToolSupport.Truncate(sb.ToString()));
    }
}

public sealed class CollectorDeleteTool(IServiceScopeFactory scopes) : ITool
{
    public bool RequiresAgentRun => true;

    public string Name => "collector_delete";

    public string Description =>
        "Deletes one of your own collection jobs by name (its already-collected data stays). Use it to remove jobs that are " +
        "redundant, failing for good, or replaced. You cannot delete the user's jobs.";

    public string ParametersSchemaJson => """
        { "type": "object", "properties": { "name": { "type": "string" } }, "required": ["name"] }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (context.AgentId is not { } agentId) return MonitoringToolSupport.NotAvailable();
        if (arguments.GetStringArg("name") is not { Length: > 0 } name) return new ToolCompleted("'name' is required.", IsError: true);

        await using var scope = scopes.CreateAsyncScope();
        var error = await scope.ServiceProvider.GetRequiredService<CollectionJobService>().DeleteByNameAsync(agentId, name, JobSources.Llm, ct);
        return error is null ? new ToolCompleted($"Deleted job '{name}'.") : new ToolCompleted(error, IsError: true);
    }
}
