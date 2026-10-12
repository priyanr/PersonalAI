using System.Text.Json.Serialization;

namespace PersonalAI.Sandbox.Protocol;

/// <summary>Every message exchanged over the one WebSocket between the API and PersonalAI.SandboxHost.
/// WebSocket already frames messages via the FIN bit, so one JSON document per message needs no length
/// prefixing — polymorphism is resolved purely by the "$type" discriminator below.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(HostHello), "host.hello")]
[JsonDerivedType(typeof(HostHelloAck), "host.helloAck")]
[JsonDerivedType(typeof(Heartbeat), "heartbeat")]
[JsonDerivedType(typeof(ListSandboxesRequest), "sandbox.list")]
[JsonDerivedType(typeof(ListSandboxesResponse), "sandbox.list.result")]
[JsonDerivedType(typeof(CreateSandboxRequest), "sandbox.create")]
[JsonDerivedType(typeof(CreateSandboxResponse), "sandbox.create.result")]
[JsonDerivedType(typeof(ExecRequest), "sandbox.exec")]
[JsonDerivedType(typeof(ExecOutputChunk), "sandbox.exec.chunk")]
[JsonDerivedType(typeof(ExecCompleted), "sandbox.exec.completed")]
[JsonDerivedType(typeof(FileOpRequest), "sandbox.file")]
[JsonDerivedType(typeof(FileOpResponse), "sandbox.file.result")]
[JsonDerivedType(typeof(DestroySandboxRequest), "sandbox.destroy")]
[JsonDerivedType(typeof(DestroySandboxResponse), "sandbox.destroy.result")]
[JsonDerivedType(typeof(ErrorFrame), "error")]
public abstract record SandboxFrame(Guid RequestId);

public enum SandboxKind { Session, Ephemeral }
public enum StreamKind { Stdout, Stderr }

/// No JsonStringEnumConverter is registered for the protocol (see SandboxJson) — this enum serializes as
/// a plain integer ordinal on the wire. New values MUST be appended at the end, never inserted, or a
/// deployed broker/API pair on different builds will silently desync (the same ordinal meaning two
/// different operations on each side).
public enum FileOp { Read, Write, Edit, List, Search, Delete, Move, MakeDirectory, ReadRaw, Zip }

public sealed record HostHello(Guid RequestId, string HostId, string DockerVersion, int ProtocolVersion) : SandboxFrame(RequestId);
public sealed record HostHelloAck(Guid RequestId, bool Accepted, string? Reason) : SandboxFrame(RequestId);
public sealed record Heartbeat(Guid RequestId, DateTimeOffset SentAtUtc) : SandboxFrame(RequestId);

public sealed record AdoptedSandbox(Guid SandboxId, string ContainerId, string Status, DateTimeOffset CreatedAtUtc);
public sealed record ListSandboxesRequest(Guid RequestId) : SandboxFrame(RequestId);
public sealed record ListSandboxesResponse(Guid RequestId, IReadOnlyList<AdoptedSandbox> Sandboxes) : SandboxFrame(RequestId);

public sealed record CreateSandboxRequest(
    Guid RequestId, Guid SandboxId, SandboxKind Kind, Guid OwnerId, string Image,
    int? CpuLimitMilliCores, long? MemoryLimitBytes, int? PidsLimit,
    bool RunAsNonRoot, IReadOnlyList<string> CapDrop,
    /// Ports (host == container number) to try to reuse from a previous create of this same sandbox —
    /// empty on first creation. The broker falls back to freshly allocated ports for any of these that
    /// are no longer free, so this is a preference, not a guarantee.
    IReadOnlyList<int> PreferredPorts) : SandboxFrame(RequestId);
public sealed record CreateSandboxResponse(
    Guid RequestId, bool Success, string? ContainerId, string? HostPath,
    /// The ports actually published, host port == container port, TCP. Always Sandbox:ExposedPortCount
    /// entries on success (Broker:ExposedPortCount on the broker's own config) — a container the model
    /// starts a server in must bind to one of these to be reachable from outside it.
    IReadOnlyList<int> Ports,
    string? Error) : SandboxFrame(RequestId);

public sealed record ExecRequest(Guid RequestId, Guid SandboxId, IReadOnlyList<string> Command,
    string? WorkingDir, int TimeoutSeconds) : SandboxFrame(RequestId);
public sealed record ExecOutputChunk(Guid RequestId, StreamKind Stream, long Sequence, string DataBase64) : SandboxFrame(RequestId);
/// <param name="ContainerMissing">True when the broker could not exec because the container itself is
/// gone (removed, or exists but not running) rather than the command failing normally — the signal
/// SandboxTool uses to mark the DB row Orphaned, recreate, and retry once instead of surfacing a bare
/// error. Defaults false so an old build on either end of the wire still deserializes this frame.</param>
public sealed record ExecCompleted(Guid RequestId, int? ExitCode, bool TimedOut, string? Error,
    bool ContainerMissing = false) : SandboxFrame(RequestId);

/// One frame for every filesystem operation — they all reduce to a path plus a couple of operands, and
/// the broker services them as direct I/O under the owner's host directory (never through Docker).
public sealed record FileOpRequest(
    Guid RequestId, Guid SandboxId, FileOp Op, string Path,
    string? DestinationPath = null,   // Move
    string? ContentBase64 = null,     // Write
    string? OldText = null,           // Edit — exact string to replace
    string? NewText = null,           // Edit
    string? Pattern = null,           // Search — regex over file contents
    bool Append = false,              // Write
    bool Recursive = false,           // List, Delete
    int Offset = 0, int Limit = 0,    // Read — line window; 0 = unbounded
    long MaxBytes = 0,
    IReadOnlyList<string>? SelectedPaths = null) : SandboxFrame(RequestId); // Zip — specific relative
                                       // files/dirs to include; null/empty means "everything under Path"

public sealed record FileEntry(string Path, bool IsDirectory, long Size, DateTimeOffset ModifiedAtUtc);
public sealed record SearchHit(string Path, int LineNumber, string Line);
/// <param name="ContainerMissing">True when the broker couldn't even resolve the sandbox's owning
/// container (no cached record and no matching container found by Docker label lookup) — same self-heal
/// signal as ExecCompleted.ContainerMissing. Defaults false for wire backward-compat.</param>
public sealed record FileOpResponse(
    Guid RequestId, bool Success, string? Error,
    string? ContentBase64 = null, bool Truncated = false, long TotalSize = 0,
    IReadOnlyList<FileEntry>? Entries = null,
    IReadOnlyList<SearchHit>? Hits = null,
    int ReplacementCount = 0,
    bool ContainerMissing = false) : SandboxFrame(RequestId);

/// DeleteFiles removes the owner's host directory too — used for ephemeral runs and session deletion,
/// but NOT when the reaper merely retires an idle chat container.
public sealed record DestroySandboxRequest(Guid RequestId, Guid SandboxId, bool DeleteFiles) : SandboxFrame(RequestId);
public sealed record DestroySandboxResponse(Guid RequestId, bool Success, string? Error) : SandboxFrame(RequestId);
public sealed record ErrorFrame(Guid RequestId, string Message) : SandboxFrame(RequestId);
