using System.Text.Json;

namespace PersonalAI.Core.Models.DTOs;

public record CollectionJobDto(
    Guid Id, string Name, string? Description, string Tool, string Request, int IntervalMinutes,
    JsonElement Parser, JsonElement Dataset, List<string>? Targets, string Status, string Source,
    DateTimeOffset? LastRunAt, string? LastError, int? LastRowCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string ApprovalToken, bool OutsideAllowlist);

/// <summary>Approval names the exact content being approved: the job's <c>approvalToken</c> as the page loaded it.</summary>
public record ApproveJobRequest(string? Token);

public record RunNowItemDto(string Job, string Target, bool Success, int Rows, string? Error);
public record RunNowResultDto(int JobsRun, IReadOnlyList<RunNowItemDto> Results);
