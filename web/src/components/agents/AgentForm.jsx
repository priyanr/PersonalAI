"use client";

import { useState } from "react";
import DataSourceScopes from "./DataSourceScopes";
import { useModels } from "@/hooks/useModels";
import ModelSelect from "@/components/models/ModelSelect";
import { useProviders } from "@/hooks/useProviders";
import { useSkills } from "@/hooks/useSkills";
import {
  AGENT_SCHEDULE_KIND_OPTIONS,
  AGENT_TYPE_OPTIONS,
  AgentScheduleKind,
  AgentType,
  CONTEXT_SIZE_OPTIONS,
  DASHBOARD_COLLECT_INTERVAL_RANGE,
  REASONING_EFFORT_OPTIONS,
  SKILL_TOOL_OPTIONS
} from "@/lib/enums";

function toggle(list, value) {
  return list.includes(value) ? list.filter((v) => v !== value) : [...list, value];
}

function toDatetimeLocalValue(isoString) {
  if (!isoString) return "";
  const d = new Date(isoString);
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function fromDatetimeLocalValue(value) {
  return value ? new Date(value).toISOString() : null;
}

export default function AgentForm({ initial, onSubmit, submitLabel, submitting, error }) {
  const { providers } = useProviders();
  const { skills } = useSkills();
  const [type, setType] = useState(initial?.type ?? AgentType.General);
  const [collectIntervalMinutes, setCollectIntervalMinutes] = useState(
    String(initial?.typeConfig?.collectIntervalMinutes ?? 5)
  );
  const [requireJobApproval, setRequireJobApproval] = useState(initial?.typeConfig?.requireJobApproval ?? true);
  // What the agent may reach, per data tool. An agent saved before data sources were generic has targetGroupIds: those are its SSH scope.
  const [toolScopes, setToolScopes] = useState(
    initial?.typeConfig?.toolScopes ??
      (initial?.typeConfig?.targetGroupIds?.length ? { ssh: initial.typeConfig.targetGroupIds } : {})
  );
  const [retentionDays, setRetentionDays] = useState(String(initial?.typeConfig?.retentionDays ?? 30));
  const [name, setName] = useState(initial?.name ?? "");
  const [description, setDescription] = useState(initial?.description ?? "");
  const [prompt, setPrompt] = useState(initial?.prompt ?? "");
  const [providerKey, setProviderKey] = useState(initial?.providerKey ?? "ollama");
  const { models, status: modelsStatus } = useModels(providerKey);
  const [modelName, setModelName] = useState(initial?.modelName ?? "");
  const [numCtx, setNumCtx] = useState(initial?.numCtx ? String(initial.numCtx) : "");
  const [enabledSkillIds, setEnabledSkillIds] = useState(initial?.enabledSkillIds ?? []);
  const [enabledTools, setEnabledTools] = useState(initial?.enabledTools ?? []);
  const [maxLlmCalls, setMaxLlmCalls] = useState(initial?.maxLlmCalls ? String(initial.maxLlmCalls) : "");
  const [reasoningEffort, setReasoningEffort] = useState(initial?.reasoningEffort ?? "");

  const [scheduleKind, setScheduleKind] = useState(initial?.scheduleKind ?? AgentScheduleKind.Manual);
  const [scheduleOnceAt, setScheduleOnceAt] = useState(toDatetimeLocalValue(initial?.scheduleOnceAt));
  const [intervalMinutes, setIntervalMinutes] = useState(
    initial?.intervalMinutes ? String(initial.intervalMinutes) : "60"
  );
  const [cronExpression, setCronExpression] = useState(initial?.cronExpression ?? "");
  const [enabled, setEnabled] = useState(initial?.enabled ?? true);

  // Model list is provider-scoped — a model chosen under one provider isn't valid under another,
  // so switching provider (on create or edit) clears the model.
  function handleProviderChange(next) {
    if (next === providerKey) return;
    setProviderKey(next);
    setModelName("");
  }

  function handleSubmit(e) {
    e.preventDefault();
    onSubmit({
      type,
      typeConfig:
        type === AgentType.DashboardAlerts
          ? {
              toolScopes,
              collectIntervalMinutes: Number(collectIntervalMinutes),
              requireJobApproval,
              retentionDays: Number(retentionDays)
            }
          : {},
      name: name.trim(),
      description: description.trim() || null,
      prompt,
      providerKey,
      modelName,
      numCtx: numCtx ? Number(numCtx) : null,
      enabledSkillIds,
      enabledTools,
      maxLlmCalls: maxLlmCalls ? Number(maxLlmCalls) : null,
      reasoningEffort: reasoningEffort || null,
      scheduleKind,
      scheduleOnceAt: scheduleKind === AgentScheduleKind.Once ? fromDatetimeLocalValue(scheduleOnceAt) : null,
      intervalMinutes: scheduleKind === AgentScheduleKind.Interval ? Number(intervalMinutes) : null,
      cronExpression: scheduleKind === AgentScheduleKind.Cron ? cronExpression.trim() : null,
      enabled
    });
  }

  return (
    <form className="skill-form" onSubmit={handleSubmit}>
      <label>
        Agent type
        <select value={type} onChange={(e) => setType(Number(e.target.value))}>
          {AGENT_TYPE_OPTIONS.map((opt) => (
            <option key={opt.value} value={opt.value}>
              {opt.label}
            </option>
          ))}
        </select>
      </label>
      <p className="muted">{AGENT_TYPE_OPTIONS.find((o) => o.value === type)?.description}</p>

      <label>
        Name
        <input type="text" value={name} onChange={(e) => setName(e.target.value)} required />
      </label>

      <label>
        Description (optional)
        <input type="text" value={description} onChange={(e) => setDescription(e.target.value)} />
      </label>

      <label>
        Prompt
        <textarea
          rows={8}
          placeholder="Instructions for how/when this agent should invoke its allowed skills."
          value={prompt}
          onChange={(e) => setPrompt(e.target.value)}
          required
        />
      </label>

      <div className="skill-form-row">
        {providers.length > 1 && (
          <label>
            Provider
            <select value={providerKey} onChange={(e) => handleProviderChange(e.target.value)}>
              {providers.map((p) => (
                <option key={p.key} value={p.key}>
                  {p.displayName}
                </option>
              ))}
            </select>
          </label>
        )}
        <label>
          Model
          <ModelSelect
            key={providerKey}
            models={models}
            value={modelName}
            onChange={setModelName}
            loading={modelsStatus === "loading"}
            required
          />
        </label>
        <label>
          Context size
          <select value={numCtx} onChange={(e) => setNumCtx(e.target.value)}>
            {CONTEXT_SIZE_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </label>
        <label>
          Max LLM calls per run
          <input
            type="number"
            min={1}
            placeholder="Default"
            value={maxLlmCalls}
            onChange={(e) => setMaxLlmCalls(e.target.value)}
          />
        </label>
        <label>
          Reasoning effort
          <select value={reasoningEffort} onChange={(e) => setReasoningEffort(e.target.value)}>
            {REASONING_EFFORT_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </label>
      </div>
      <p className="muted">
        Caps how many model calls this agent may make in one run (each counting a skill/tool round-trip)
        before it&apos;s stopped. The agent is told its current call number and this budget on every call,
        so it can wrap up in time. Leave blank to use the server default. Reasoning effort only affects
        reasoning models; &quot;Model default&quot; uses the model&apos;s setting on the Models page.
      </p>

      {type === AgentType.DashboardAlerts && (
        <fieldset className="skill-form-fieldset">
          <legend>Data sources</legend>
          <DataSourceScopes value={toolScopes} onChange={setToolScopes} />
        </fieldset>
      )}

      {type === AgentType.DashboardAlerts && (
        <fieldset className="skill-form-fieldset">
          <legend>Dashboard &amp; alerts settings</legend>
          <div className="skill-form-row">
            <label>
              Default job interval (minutes)
              <input
                type="number"
                min={DASHBOARD_COLLECT_INTERVAL_RANGE.min}
                max={DASHBOARD_COLLECT_INTERVAL_RANGE.max}
                value={collectIntervalMinutes}
                onChange={(e) => setCollectIntervalMinutes(e.target.value)}
                required
              />
            </label>
            <label>
              Keep raw data (days)
              <input
                type="number"
                min={1}
                value={retentionDays}
                onChange={(e) => setRetentionDays(e.target.value)}
                required
              />
            </label>
          </div>
          <label className="settings-checkbox">
            <input type="checkbox" checked={requireJobApproval} onChange={(e) => setRequireJobApproval(e.target.checked)} />
            Require my approval before collection jobs the model creates can run (recommended)
          </label>
          <p className="muted">
            The model designs collection jobs (a request to one of your data sources, how to parse it, where to store it)
            in the run below; once approved, they run on their own intervals without the model. With approval on, you see
            the exact request first. The Prompt and Schedule control the model run that designs jobs, the dashboard and
            the alert rules. Each data tool limits what the model may send in its own settings under Settings &gt; Tools
            (for SSH, the allowed commands). Anything outside a tool&apos;s allowed list always needs your approval, even with
            approval turned off here.
          </p>
        </fieldset>
      )}

      <fieldset className="skill-form-fieldset">
        <legend>Skills this agent may invoke</legend>
        {skills.length === 0 ? (
          <p className="muted">
            No skills configured yet — create one on the Skills page to make it available here.
          </p>
        ) : (
          <div className="tool-toggles-group">
            {skills.map((s) => (
              <label key={s.id} className="settings-checkbox settings-checkbox-sub">
                <input
                  type="checkbox"
                  checked={enabledSkillIds.includes(s.id)}
                  onChange={() => setEnabledSkillIds((prev) => toggle(prev, s.id))}
                />
                {s.name}
              </label>
            ))}
          </div>
        )}
      </fieldset>

      <fieldset className="skill-form-fieldset">
        <legend>Tools this agent may call directly</legend>
        <p className="muted">
          For one-off tool use that doesn&apos;t need a reusable Skill wrapper. The agent calls these by
          their own tool name, alongside any Skills enabled above.
        </p>
        <div className="tool-toggles-group">
          {SKILL_TOOL_OPTIONS.map((opt) => (
            <label key={opt.name} className="settings-checkbox settings-checkbox-sub">
              <input
                type="checkbox"
                checked={enabledTools.includes(opt.name)}
                onChange={() => setEnabledTools((prev) => toggle(prev, opt.name))}
              />
              {opt.label}
            </label>
          ))}
        </div>
      </fieldset>

      <fieldset className="skill-form-fieldset">
        <legend>Schedule</legend>
        <div className="agent-schedule-fields">
          <label>
            When
            <select value={scheduleKind} onChange={(e) => setScheduleKind(Number(e.target.value))}>
              {AGENT_SCHEDULE_KIND_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          </label>

          {scheduleKind === AgentScheduleKind.Once && (
            <label>
              Run at
              <input
                type="datetime-local"
                value={scheduleOnceAt}
                onChange={(e) => setScheduleOnceAt(e.target.value)}
                required
              />
            </label>
          )}

          {scheduleKind === AgentScheduleKind.Interval && (
            <label>
              Every (minutes)
              <input
                type="number"
                min={1}
                value={intervalMinutes}
                onChange={(e) => setIntervalMinutes(e.target.value)}
                required
              />
            </label>
          )}

          {scheduleKind === AgentScheduleKind.Cron && (
            <label>
              Cron expression (UTC)
              <input
                type="text"
                placeholder="0 9 * * *"
                value={cronExpression}
                onChange={(e) => setCronExpression(e.target.value)}
                required
              />
            </label>
          )}

          {scheduleKind !== AgentScheduleKind.Manual && (
            <label className="settings-checkbox">
              <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
              Scheduler enabled
            </label>
          )}
        </div>
        {scheduleKind === AgentScheduleKind.Cron && (
          <p className="muted">Standard 5-field cron: minute hour day-of-month month day-of-week, evaluated in UTC.</p>
        )}
      </fieldset>

      <button type="submit" disabled={submitting || !modelName}>
        {submitting ? "Saving..." : submitLabel}
      </button>
      {error && <p className="error-text">{error}</p>}
    </form>
  );
}
