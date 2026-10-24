using System.Text.Json;
using PersonalAI.Core.Infrastructure;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Models.Entities;
using PersonalAI.Core.Models.Entities.Enums;
using PersonalAI.Core.Services.Scheduling;
using AgentEntity = PersonalAI.Core.Models.Entities.Agent;

namespace PersonalAI.Tests;

/// <summary>Inserts rows straight into the database to set up a test, now that repositories only read.</summary>
public static class TestSeed
{
    public static async Task<Skill> SkillAsync(
        AppDbContext db, string name, string? description, string prompt, string providerKey, string modelName, int? numCtx,
        IReadOnlyList<string> enabledTools, IReadOnlyList<string> secretKeys, CancellationToken ct)
    {
        var skill = new Skill
        {
            Name = name, Description = description, Prompt = prompt, ProviderKey = providerKey, ModelName = modelName,
            NumCtx = numCtx, EnabledToolsJson = JsonSerializer.Serialize(enabledTools), SecretKeysJson = JsonSerializer.Serialize(secretKeys)
        };
        db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        return skill;
    }

    public static async Task<AgentEntity> AgentAsync(
        AppDbContext db, string name, string? description, string prompt, string providerKey, string modelName, int? numCtx,
        IReadOnlyList<Guid> enabledSkillIds, IReadOnlyList<string> enabledTools, int? maxLlmCalls, AgentScheduleKind scheduleKind,
        DateTimeOffset? scheduleOnceAt, int? intervalMinutes, string? cronExpression, bool enabled, CancellationToken ct,
        string? reasoningEffort = null, AgentType type = AgentType.General, string typeConfigJson = "{}")
    {
        var agent = new AgentEntity
        {
            Type = type, TypeConfigJson = typeConfigJson, Name = name, Description = description, Prompt = prompt,
            ProviderKey = providerKey, ModelName = modelName, NumCtx = numCtx,
            EnabledSkillIdsJson = JsonSerializer.Serialize(enabledSkillIds), EnabledToolsJson = JsonSerializer.Serialize(enabledTools),
            MaxLlmCalls = maxLlmCalls, ReasoningEffort = reasoningEffort, ScheduleKind = scheduleKind, ScheduleOnceAt = scheduleOnceAt,
            IntervalMinutes = intervalMinutes, CronExpression = cronExpression, Enabled = enabled
        };
        agent.NextRunAt = ScheduleCalculator.ComputeNextRunAt(agent, DateTimeOffset.UtcNow);
        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        return agent;
    }

    public static async Task<SshServerGroup> SshGroupAsync(AppDbContext db, string name, string? description, CancellationToken ct)
    {
        var group = new SshServerGroup { Name = name, Description = description };
        db.SshServerGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public static async Task<SshServer> SshServerAsync(
        AppDbContext db, Guid groupId, string name, string address, int port, string username, string secretRef, bool enabled, CancellationToken ct)
    {
        var server = new SshServer
        {
            GroupId = groupId, Name = name, Address = address, Port = port, Username = username, SecretRef = secretRef, Enabled = enabled
        };
        db.SshServers.Add(server);
        await db.SaveChangesAsync(ct);
        return server;
    }

    public static async Task<ChatSession> SessionAsync(
        AppDbContext db, string? title, string modelName, CancellationToken ct, string providerKey = "ollama", bool toolsEnabled = true,
        int? numCtx = null, bool trimHistoryEnabled = true, bool? thinkingEnabled = null, int? maxLlmCalls = null,
        IReadOnlyList<Guid>? enabledSkillIds = null, ToolFlags? toolFlags = null, string? reasoningEffort = null)
    {
        var flags = toolFlags ?? ToolFlags.Default;
        var session = new ChatSession
        {
            Title = title, ProviderKey = providerKey, ModelName = modelName, ToolsEnabled = toolsEnabled, NumCtx = numCtx,
            TrimHistoryEnabled = trimHistoryEnabled, ThinkingEnabled = thinkingEnabled, ReasoningEffort = reasoningEffort,
            MaxLlmCalls = maxLlmCalls, EnabledSkillIdsJson = JsonSerializer.Serialize(enabledSkillIds ?? []),
            WebSearchEnabled = flags.WebSearchEnabled, ReadFileEnabled = flags.ReadFileEnabled, WriteFileEnabled = flags.WriteFileEnabled,
            EditFileEnabled = flags.EditFileEnabled, ListFilesEnabled = flags.ListFilesEnabled, SearchFilesEnabled = flags.SearchFilesEnabled,
            NetworkScanEnabled = flags.NetworkScanEnabled, HttpRequestEnabled = flags.HttpRequestEnabled,
            ComputerControlEnabled = flags.ComputerControlEnabled, SandboxEnabled = flags.SandboxEnabled, GmailEnabled = flags.GmailEnabled
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>Appends a message the way ChatAgentService does: the next SequenceNumber in its session.</summary>
    public static async Task<ChatMessage> MessageAsync(AppDbContext db, ChatMessage message, CancellationToken ct)
    {
        message.SequenceNumber = (db.ChatMessages.Where(m => m.SessionId == message.SessionId).Max(m => (int?)m.SequenceNumber) ?? 0) + 1;
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(ct);
        return message;
    }

    public static async Task<ToolInvocation> ToolInvocationAsync(AppDbContext db, ToolInvocation invocation, CancellationToken ct)
    {
        db.ToolInvocations.Add(invocation);
        await db.SaveChangesAsync(ct);
        return invocation;
    }

    public static async Task<SandboxInstance> SandboxAsync(AppDbContext db, SandboxInstance instance, CancellationToken ct)
    {
        db.SandboxInstances.Add(instance);
        await db.SaveChangesAsync(ct);
        return instance;
    }

    public static async Task<AgentRun> RunAsync(AppDbContext db, Guid agentId, AgentRunTrigger trigger)
    {
        var run = new AgentRun { AgentId = agentId, Trigger = trigger };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }
}
