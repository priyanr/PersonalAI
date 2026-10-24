using System.Text.Json;

namespace PersonalAI.Core.Models.DTOs;

public record DashboardDto(int Version, string? Reason, DateTimeOffset UpdatedAt, JsonElement Spec);
public record DashboardVersionDto(int Version, string? Reason, Guid? AgentRunId, DateTimeOffset CreatedAt);
public record DashboardDataDto(DateTimeOffset GeneratedAt, Dictionary<string, object> Widgets);
