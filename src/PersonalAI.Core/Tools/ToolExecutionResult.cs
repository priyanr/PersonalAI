namespace PersonalAI.Core.Tools;

/// <summary>Which kind of run owns a sandbox (see SandboxTool) — a chat session's sandbox survives
/// across turns, while a skill/agent run's sandbox is ephemeral and torn down with the run.</summary>
public enum SandboxOwnerKind { ChatSession, SkillRun, AgentRun }

/// <summary>Identifies the caller for SandboxTool: which owner's sandbox (get-or-create by this Id) a
/// call should run against. Absent (null) outside chat/skill/agent execution, e.g. the pre-approval
/// resume path reconstructs it from the owning session, and tests that don't touch the sandbox tool
/// never need to set it.</summary>
public readonly record struct SandboxOwner(SandboxOwnerKind Kind, Guid Id);

/// <summary>Sandbox is optional so every existing `new ToolExecutionContext(false)` construction across
/// the codebase and test suite keeps compiling — only ChatAgentService/SkillExecutionService/
/// AgentExecutionService populate it, since only they know which session/run they're serving.</summary>
public readonly record struct ToolExecutionContext(bool PreApproved, SandboxOwner? Sandbox = null, Action<string>? OnProgress = null);

public abstract record ToolExecutionResult;

public sealed record ToolCompleted(string Content, bool IsError = false) : ToolExecutionResult;

/// <summary>Returned when a file tool resolves a path outside the workspace sandbox and the caller
/// has not already pre-approved that specific invocation.</summary>
public sealed record ToolApprovalRequired(string Reason, string ResolvedPath) : ToolExecutionResult;
