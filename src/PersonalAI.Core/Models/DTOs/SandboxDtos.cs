using PersonalAI.Core.Models.Entities.Enums;

namespace PersonalAI.Core.Models.DTOs;

/// <summary>Rendered by GET /api/sandbox/status for the /settings/tools "live broker indicator" (a later
/// phase's frontend concern) — ActiveSandboxes counts SandboxInstance rows currently Running, independent
/// of whether the broker connection itself is up. RemoteIp is the connected broker's source address (null
/// when nothing is connected) — the same address SandboxTool's "ports" action gives the model as the host
/// half of a published port's http://host:port URL.</summary>
public record SandboxStatusDto(bool Connected, string? HostId, DateTimeOffset? ConnectedAt, int ActiveSandboxes, string? RemoteIp);

/// <summary>Rendered by GET /api/sandbox/{ownerId}/status — what the frontend polls to decide whether to
/// show the "Sandbox files" button for a chat session or agent run. CanBrowse folds together "the broker
/// is connected" and "this owner has a sandbox that isn't permanently gone" (see
/// SandboxProvisioningService.TryEnsureExistingAsync) into the one bit the button actually needs.</summary>
public record SandboxOwnerStatusDto(bool CanBrowse, SandboxInstanceStatus? Status, IReadOnlyList<int> Ports);

/// <summary>Rendered by GET /api/sandbox/{ownerId}/files/content. IsText false means the file failed a
/// strict UTF-8 decode (or contains an embedded NUL) — Content is omitted and the frontend offers download
/// only, since there's no safe way to show or edit it as text.</summary>
public record SandboxFileContentDto(bool IsText, string? Content, bool Truncated, long TotalSize);

/// <summary>Body of PUT /api/sandbox/{ownerId}/files/content — a full overwrite from a plain textarea
/// editor, not the model's find/replace Edit op.</summary>
public record SandboxFileWriteRequest(string Path, string? Content);

/// <summary>Body of POST /api/sandbox/{ownerId}/files/zip. Paths null/empty means "everything".</summary>
public record SandboxZipRequest(IReadOnlyList<string>? Paths);

public record SandboxMkdirRequest(string Path);
public record SandboxDeleteRequest(string Path, bool Recursive);
public record SandboxMoveRequest(string Path, string Destination);
