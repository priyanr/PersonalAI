// Mirrors the C# enums' declaration order — System.Text.Json serializes enums as their
// underlying integer by default, so these numeric values must stay in sync with
// PersonalAI.Core/Models/Entities/Enums/*.cs.

export const MessageRole = { User: 0, Assistant: 1, Tool: 2, System: 3 };

export const SessionStatus = { Active: 0, AwaitingApproval: 1 };

export const ToolInvocationStatus = { Completed: 0, PendingApproval: 1, Approved: 2, Denied: 3, Failed: 4 };

// Common Ollama context window sizes. An empty string means "model default" (numCtx: null),
// i.e. don't send options.num_ctx at all and let Ollama pick. `short` labels the chat options'
// segmented control.
export const CONTEXT_SIZE_OPTIONS = [
  { value: "", label: "Model default", short: "Default" },
  { value: "4096", label: "4,096", short: "4K" },
  { value: "8192", label: "8,192", short: "8K" },
  { value: "16384", label: "16,384", short: "16K" },
  { value: "32768", label: "32,768", short: "32K" },
  { value: "65536", label: "65,536", short: "64K" },
  { value: "131072", label: "131,072", short: "128K" }
];

// Mirrors ChatSession's per-tool bool properties / ToolFlags DTO (PersonalAI.Core/Models/DTOs/SessionDtos.cs).
// `group` places the tool in the chat options sheet: "web", "files", or "outside" — tools that act beyond
// this chat's own workspace, shown in the amber "needs your decision" tier.
export const TOOL_OPTIONS = [
  { key: "webSearchEnabled", label: "Web search", group: "web", description: "Searches the web through SearXNG" },
  { key: "httpRequestEnabled", label: "HTTP request", group: "web", description: "Fetches or calls any URL" },
  { key: "readFileEnabled", label: "Read", group: "files", description: "Read files" },
  { key: "listFilesEnabled", label: "List", group: "files", description: "List files" },
  { key: "searchFilesEnabled", label: "Search", group: "files", description: "Search files" },
  { key: "writeFileEnabled", label: "Write", group: "files", description: "Write files" },
  { key: "editFileEnabled", label: "Edit", group: "files", description: "Edit files" },
  // Off by default: this tool runs arbitrary commands inside a Docker container on the sandbox host,
  // so each session must explicitly opt in.
  { key: "sandboxEnabled", label: "Sandbox", group: "outside", defaultEnabled: false, description: "Runs commands and edits files in a container" },
  // Off by default: this tool can read and send email from the user's connected Gmail account, so each
  // session must explicitly opt in.
  { key: "gmailEnabled", label: "Gmail", group: "outside", defaultEnabled: false, description: "Reads and sends email from your account" },
  // Off by default: this tool probes other devices on the user's LAN, not just this session's own
  // sandbox, so each session must explicitly opt in.
  { key: "networkScanEnabled", label: "Network scan", group: "outside", defaultEnabled: false, description: "Probes devices on your local network" },
  // Off by default: this tool can drive the mouse/keyboard/screen of the entire host machine, not
  // just this session's own sandbox, so each session must explicitly opt in.
  { key: "computerControlEnabled", label: "Computer control", group: "outside", defaultEnabled: false, description: "Drives this machine's mouse, keyboard and screen" }
];

export const DEFAULT_TOOL_FLAGS = Object.fromEntries(TOOL_OPTIONS.map(t => [t.key, t.defaultEnabled ?? true]));

// A Skill's EnabledTools / an Agent's EnabledTools (direct tool calls) are both a flat allowlist of
// ITool.Name values (see PersonalAI.Core/Tools/*), unlike ChatSession's opt-out per-tool booleans —
// nothing is enabled by default. Shared by SkillForm and AgentForm.
export const SKILL_TOOL_OPTIONS = [
  { name: "web_search", label: "Web search" },
  { name: "read_file", label: "Read files" },
  { name: "write_file", label: "Write files" },
  { name: "edit_file", label: "Edit files" },
  { name: "list_files", label: "List files" },
  { name: "search_files", label: "Search files" },
  { name: "network_scan", label: "Network scan (LAN recon)" },
  { name: "http_request", label: "HTTP request" },
  { name: "gmail", label: "Gmail" },
  { name: "computer_control", label: "Computer control (mouse/keyboard/screenshot)" },
  { name: "sandbox", label: "Sandbox (run commands and edit files in a container)" }
];

// Mirrors PersonalAI.Core/Models/Entities/Enums/AgentScheduleKind.cs.
export const AgentScheduleKind = { Manual: 0, Once: 1, Interval: 2, Cron: 3 };

export const AGENT_SCHEDULE_KIND_OPTIONS = [
  { value: AgentScheduleKind.Manual, label: "Manual only" },
  { value: AgentScheduleKind.Once, label: "Once, at a specific time" },
  { value: AgentScheduleKind.Interval, label: "Every N minutes" },
  { value: AgentScheduleKind.Cron, label: "Cron expression" }
];

// Mirrors PersonalAI.Core/Models/Entities/Enums/AgentRunTrigger.cs.
export const AgentRunTrigger = { Manual: 0, Scheduled: 1 };

// Mirrors PersonalAI.Core/Models/Entities/Enums/AgentRunStatus.cs.
export const AgentRunStatus = { Running: 0, Succeeded: 1, Failed: 2 };

export const AGENT_RUN_STATUS_LABELS = { 0: "Running", 1: "Succeeded", 2: "Failed" };
export const AGENT_RUN_TRIGGER_LABELS = { 0: "Manual", 1: "Scheduled" };

// ChatSession.ThinkingEnabled is a nullable bool: null inherits the app-wide default.
// <select> values are strings, so "" / "true" / "false" map to null / true / false.
export const THINKING_OPTIONS = [
  { value: "", label: "Use app default", short: "Default" },
  { value: "true", label: "On" },
  { value: "false", label: "Off" }
];

// Mirrors ModelSettings.ReasoningEffortLevels. "" (null) leaves the model's ReasoningEffort setting from
// the Models page, else the model's own default.
export const REASONING_EFFORT_OPTIONS = [
  { value: "", label: "Model default", short: "Default" },
  { value: "none", label: "None" },
  { value: "minimal", label: "Minimal", short: "Min" },
  { value: "low", label: "Low" },
  { value: "medium", label: "Medium", short: "Med" },
  { value: "high", label: "High" },
  { value: "xhigh", label: "Extra high", short: "Max" }
];

export function thinkingValueToOption(value) {
  return value === null || value === undefined ? "" : String(value);
}

export function thinkingOptionToValue(option) {
  return option === "" ? null : option === "true";
}
