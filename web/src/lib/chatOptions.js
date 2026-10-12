import { TOOL_OPTIONS, DEFAULT_TOOL_FLAGS, CONTEXT_SIZE_OPTIONS, REASONING_EFFORT_OPTIONS } from "@/lib/enums";

// A chat's options as the options sheet edits them. numCtx / maxLlmCalls are kept as strings ("" = default)
// because they back form controls; optionsToPayload converts them for the API. providerKey / modelName are
// only present for an existing chat (the new-chat form picks its model outside the sheet).

export const TOOL_GROUPS = [
  { id: "web", title: "Web" },
  { id: "files", title: "Workspace files" },
  { id: "outside", title: "Reaches outside this chat", note: "These act on your accounts, network or machine, not just this chat's workspace." }
].map((g) => ({ ...g, tools: TOOL_OPTIONS.filter((t) => t.group === g.id) }));

const FILE_KEYS = TOOL_OPTIONS.filter((t) => t.group === "files").map((t) => t.key);
const READ_ONLY_KEYS = ["readFileEnabled", "listFilesEnabled", "searchFilesEnabled"];

export const FILE_PRESETS = [
  { id: "all", label: "All", keys: FILE_KEYS },
  { id: "read-only", label: "Read-only", keys: READ_ONLY_KEYS },
  { id: "none", label: "None", keys: [] }
];

export function applyFilePreset(toolFlags, preset) {
  return { ...toolFlags, ...Object.fromEntries(FILE_KEYS.map((k) => [k, preset.keys.includes(k)])) };
}

export function activeFilePreset(toolFlags) {
  return FILE_PRESETS.find((p) => FILE_KEYS.every((k) => Boolean(toolFlags[k]) === p.keys.includes(k)))?.id ?? null;
}

export function defaultOptions() {
  return {
    toolsEnabled: true,
    toolFlags: DEFAULT_TOOL_FLAGS,
    enabledSkillIds: [],
    thinkingEnabled: null,
    reasoningEffort: null,
    numCtx: "",
    maxLlmCalls: "",
    trimHistoryEnabled: true
  };
}

export function sessionToOptions(session) {
  return {
    providerKey: session.providerKey,
    modelName: session.modelName,
    toolsEnabled: session.toolsEnabled,
    toolFlags: session.toolFlags ?? DEFAULT_TOOL_FLAGS,
    enabledSkillIds: session.enabledSkillIds ?? [],
    thinkingEnabled: session.thinkingEnabled ?? null,
    reasoningEffort: session.reasoningEffort ?? null,
    numCtx: session.numCtx != null ? String(session.numCtx) : "",
    maxLlmCalls: session.maxLlmCalls ? String(session.maxLlmCalls) : "",
    trimHistoryEnabled: session.trimHistoryEnabled
  };
}

export function optionsToPayload(opts) {
  return {
    ...opts,
    numCtx: opts.numCtx ? Number(opts.numCtx) : null,
    maxLlmCalls: opts.maxLlmCalls ? Number(opts.maxLlmCalls) : null
  };
}

export function optionsEqual(a, b) {
  const sameSkills =
    a.enabledSkillIds.length === b.enabledSkillIds.length && a.enabledSkillIds.every((id) => b.enabledSkillIds.includes(id));
  return (
    sameSkills &&
    TOOL_OPTIONS.every((t) => Boolean(a.toolFlags[t.key]) === Boolean(b.toolFlags[t.key])) &&
    ["providerKey", "modelName", "toolsEnabled", "thinkingEnabled", "reasoningEffort", "numCtx", "maxLlmCalls", "trimHistoryEnabled"].every(
      (k) => a[k] === b[k]
    )
  );
}

/** Short runs for the one-line summary. Tools in the "outside" tier are named so risky access stays visible. */
export function summarize(opts, skills = []) {
  const on = TOOL_OPTIONS.filter((t) => opts.toolFlags[t.key]);
  const knownSkills = opts.enabledSkillIds.filter((id) => skills.some((s) => s.id === id));
  const effort = REASONING_EFFORT_OPTIONS.find((o) => o.value === (opts.reasoningEffort ?? ""));
  const ctx = CONTEXT_SIZE_OPTIONS.find((o) => o.value === opts.numCtx);
  return {
    model: opts.modelName ?? null,
    toolsOff: !opts.toolsEnabled,
    toolCount: on.length,
    toolTotal: TOOL_OPTIONS.length,
    outside: opts.toolsEnabled ? on.filter((t) => t.group === "outside").map((t) => t.label) : [],
    skillCount: opts.toolsEnabled ? knownSkills.length : 0,
    thinking: opts.thinkingEnabled === null ? null : opts.thinkingEnabled ? "on" : "off",
    reasoning: opts.reasoningEffort ? effort?.label.toLowerCase() : null,
    context: opts.numCtx ? ctx?.short : null
  };
}
