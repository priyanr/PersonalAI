namespace PersonalAI.Core.Models.DTOs;

public record SshServerGroupDto(Guid Id, string Name, string? Description, int ServerCount, DateTimeOffset CreatedAt);
public record SaveSshServerGroupRequest(string Name, string? Description);

public record SshServerDto(
    Guid Id,
    Guid GroupId,
    string Name,
    string Address,
    int Port,
    string Username,
    string SecretRef,
    bool Enabled,
    string? HostKeyFingerprint,
    DateTimeOffset? LastSeenAt,
    string? LastError,
    DateTimeOffset CreatedAt);

public record SaveSshServerRequest(
    Guid GroupId,
    string Name,
    string Address,
    int? Port,
    string Username,
    string SecretRef,
    bool Enabled = true);

public record SshServerTestResultDto(bool Success, string? ServerInfo, string? Error, string? HostKeyFingerprint);

public record DatasetDto(string Name, string Kind, string? SchemaHint, long PointCount, DateTime? LastTs);
public record DataPointDto(string EntityKey, DateTime Ts, string ValuesJson);

