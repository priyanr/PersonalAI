"use client";

import { useEffect, useId, useRef, useState } from "react";
import Switch from "@/components/ui/Switch";
import ModelSelect from "@/components/models/ModelSelect";
import { useModels } from "@/hooks/useModels";
import { useProviders } from "@/hooks/useProviders";
import Segmented from "@/components/ui/Segmented";
import {
  CONTEXT_SIZE_OPTIONS,
  REASONING_EFFORT_OPTIONS,
  THINKING_OPTIONS,
  thinkingOptionToValue,
  thinkingValueToOption
} from "@/lib/enums";
import { TOOL_GROUPS, FILE_PRESETS, applyFilePreset, activeFilePreset, optionsEqual } from "@/lib/chatOptions";

const SKILL_FILTER_THRESHOLD = 6;
const FOCUSABLE = 'button:not(:disabled), input:not(:disabled), [tabindex]:not([tabindex="-1"])';

/** Provider + model picker, shown only for an existing chat. Changing provider clears the model, since model
 *  lists are provider-scoped. */
function ModelSection({ providerKey, modelName, onChange }) {
  const { providers } = useProviders();
  const { models, status } = useModels(providerKey);
  const providerName = providers.find((p) => p.key === providerKey)?.displayName ?? providerKey;

  return (
    <section className="sheet-section">
      <div className="sheet-section-head">
        <h3>Model</h3>
      </div>
      <div className="sheet-model">
        {providers.length > 1 && (
          <select
            aria-label="Provider"
            value={providerKey}
            onChange={(e) => onChange({ providerKey: e.target.value, modelName: "" })}
          >
            {providers.map((p) => (
              <option key={p.key} value={p.key}>
                {p.displayName}
              </option>
            ))}
          </select>
        )}
        <ModelSelect
          key={providerKey}
          models={models}
          value={modelName}
          onChange={(name) => onChange({ providerKey, modelName: name })}
          loading={status === "loading"}
        />
      </div>
      {!modelName ? (
        <p className="sheet-hint sheet-hint-attn">Choose a {providerName} model to save.</p>
      ) : status === "succeeded" && models.length === 0 ? (
        <p className="sheet-hint">No {providerName} models available. Check the provider on the Settings page.</p>
      ) : (
        <p className="sheet-hint">Applies from the next message. The conversation so far is kept.</p>
      )}
    </section>
  );
}

/**
 * Side sheet for editing a chat's model, tools, skills and model behaviour. Edits a local draft of `initial`;
 * `onSubmit(draft)` is only called from the footer button. Closing (Esc, overlay, ✕, Cancel) discards it.
 */
export default function ChatOptionsSheet({ initial, skills = [], onSubmit, onClose, submitLabel, submitting = false, error = null }) {
  const [draft, setDraft] = useState(initial);
  const [skillFilter, setSkillFilter] = useState("");
  const sheetRef = useRef(null);
  const titleId = useId();
  const thinkingId = useId();
  const reasoningId = useId();
  const contextId = useId();

  const dirty = !optionsEqual(draft, initial);
  const hasModel = "modelName" in initial;
  const missingModel = hasModel && !draft.modelName;
  const toolsOff = !draft.toolsEnabled;

  useEffect(() => {
    const opener = document.activeElement;
    sheetRef.current?.querySelector(FOCUSABLE)?.focus();
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = prevOverflow;
      opener?.focus?.();
    };
  }, []);

  function handleKeyDown(e) {
    if (e.key === "Escape") {
      e.stopPropagation();
      onClose();
      return;
    }
    if (e.key !== "Tab") return;
    const nodes = [...sheetRef.current.querySelectorAll(FOCUSABLE)];
    if (nodes.length === 0) return;
    const first = nodes[0];
    const last = nodes[nodes.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  }

  function set(key, value) {
    setDraft((prev) => ({ ...prev, [key]: value }));
  }

  function setToolFlag(key, value) {
    setDraft((prev) => ({ ...prev, toolFlags: { ...prev.toolFlags, [key]: value } }));
  }

  function toggleSkill(id) {
    setDraft((prev) => ({
      ...prev,
      enabledSkillIds: prev.enabledSkillIds.includes(id)
        ? prev.enabledSkillIds.filter((s) => s !== id)
        : [...prev.enabledSkillIds, id]
    }));
  }

  function handleSubmit(e) {
    e.preventDefault();
    onSubmit(draft);
  }

  const filePreset = activeFilePreset(draft.toolFlags);
  const query = skillFilter.trim().toLowerCase();
  const visibleSkills = query
    ? skills.filter((s) => s.name.toLowerCase().includes(query) || s.description?.toLowerCase().includes(query))
    : skills;

  return (
    <div className="sheet-overlay" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <form
        ref={sheetRef}
        className="sheet"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onKeyDown={handleKeyDown}
        onSubmit={handleSubmit}
      >
        <header className="sheet-header">
          <h2 id={titleId}>Chat options</h2>
          <button type="button" className="sheet-close" onClick={onClose} aria-label="Close">
            <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
              <path d="M2 2l10 10M12 2 2 12" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
            </svg>
          </button>
        </header>

        <div className="sheet-body">
          {hasModel && (
            <ModelSection
              providerKey={draft.providerKey}
              modelName={draft.modelName}
              onChange={(next) => setDraft((prev) => ({ ...prev, ...next }))}
            />
          )}
          <section className="sheet-section">
            <div className="sheet-section-head">
              <h3>Tools</h3>
              <Switch
                checked={draft.toolsEnabled}
                onChange={(v) => set("toolsEnabled", v)}
                ariaLabel="Let the model use tools"
              />
            </div>
            {toolsOff && <p className="sheet-hint">The model answers from its own knowledge only. Turn tools on to choose which it may use.</p>}

            <div className={`tool-groups${toolsOff ? " is-off" : ""}`} aria-disabled={toolsOff}>
              {TOOL_GROUPS.map((group) => (
                <div key={group.id} className={`tool-group tool-group--${group.id}`}>
                  <div className="tool-group-head">
                    <h4>{group.title}</h4>
                    {group.id === "files" && (
                      <Segmented
                        ariaLabel="Workspace file access"
                        options={FILE_PRESETS.map((p) => ({ value: p.id, label: p.label }))}
                        value={filePreset}
                        onChange={(id) => setDraft((prev) => ({ ...prev, toolFlags: applyFilePreset(prev.toolFlags, FILE_PRESETS.find((p) => p.id === id)) }))}
                        disabled={toolsOff}
                      />
                    )}
                  </div>
                  {group.note && <p className="tool-group-note">{group.note}</p>}

                  {group.id === "files" ? (
                    <div className="chip-toggles">
                      {group.tools.map((t) => (
                        <button
                          key={t.key}
                          type="button"
                          className="chip-toggle"
                          aria-pressed={Boolean(draft.toolFlags[t.key])}
                          disabled={toolsOff}
                          title={t.description}
                          onClick={() => setToolFlag(t.key, !draft.toolFlags[t.key])}
                        >
                          {t.label}
                        </button>
                      ))}
                    </div>
                  ) : (
                    <div className="opt-rows">
                      {group.tools.map((t) => (
                        <Switch
                          key={t.key}
                          label={t.label}
                          description={t.description}
                          checked={Boolean(draft.toolFlags[t.key])}
                          onChange={(v) => setToolFlag(t.key, v)}
                          disabled={toolsOff}
                        />
                      ))}
                    </div>
                  )}
                </div>
              ))}
            </div>
          </section>

          <section className="sheet-section">
            <div className="sheet-section-head">
              <h3>Skills</h3>
              {skills.length > 0 && !toolsOff && (
                <span className="sheet-count">
                  {draft.enabledSkillIds.filter((id) => skills.some((s) => s.id === id)).length} of {skills.length}
                </span>
              )}
            </div>
            {skills.length === 0 ? (
              <p className="sheet-hint">No skills yet. Create one under Skills and it will show up here.</p>
            ) : toolsOff ? (
              <p className="sheet-hint">Skills run through tools. Turn tools on to let this chat use them.</p>
            ) : (
              <>
                {skills.length > SKILL_FILTER_THRESHOLD && (
                  <input
                    type="search"
                    className="sheet-filter"
                    placeholder="Filter skills"
                    aria-label="Filter skills"
                    value={skillFilter}
                    onChange={(e) => setSkillFilter(e.target.value)}
                  />
                )}
                <div className="opt-rows">
                  {visibleSkills.map((s) => (
                    <Switch
                      key={s.id}
                      label={s.name}
                      description={s.description}
                      checked={draft.enabledSkillIds.includes(s.id)}
                      onChange={() => toggleSkill(s.id)}
                    />
                  ))}
                  {visibleSkills.length === 0 && <p className="sheet-hint">No skills match “{skillFilter}”.</p>}
                </div>
              </>
            )}
          </section>

          <section className="sheet-section">
            <div className="sheet-section-head">
              <h3>Model behaviour</h3>
            </div>
            <div className="field-stack">
              <div className="field">
                <span id={thinkingId} className="field-label">Thinking</span>
                <Segmented
                  ariaLabelledBy={thinkingId}
                  options={THINKING_OPTIONS}
                  value={thinkingValueToOption(draft.thinkingEnabled)}
                  onChange={(v) => set("thinkingEnabled", thinkingOptionToValue(v))}
                />
              </div>
              <div className="field">
                <span id={reasoningId} className="field-label">Reasoning effort</span>
                <Segmented
                  ariaLabelledBy={reasoningId}
                  options={REASONING_EFFORT_OPTIONS}
                  value={draft.reasoningEffort ?? ""}
                  onChange={(v) => set("reasoningEffort", v || null)}
                />
              </div>
              <div className="field">
                <span id={contextId} className="field-label">Context size</span>
                <Segmented
                  ariaLabelledBy={contextId}
                  options={CONTEXT_SIZE_OPTIONS}
                  value={draft.numCtx}
                  onChange={(v) => set("numCtx", v)}
                />
              </div>
              <label className="field field-inline">
                <span className="field-label">
                  Max LLM calls per turn
                  <span className="opt-row-desc">Stops a runaway tool loop</span>
                </span>
                <input
                  type="number"
                  min={1}
                  inputMode="numeric"
                  placeholder="Default"
                  value={draft.maxLlmCalls}
                  onChange={(e) => set("maxLlmCalls", e.target.value)}
                />
              </label>
              <Switch
                label="Trim old history"
                description="Shortens long tool results and older turns to save context"
                checked={draft.trimHistoryEnabled}
                onChange={(v) => set("trimHistoryEnabled", v)}
              />
            </div>
          </section>
        </div>

        <footer className="sheet-footer">
          <span className="sheet-status" aria-live="polite">
            {error ? <span className="error-text">{error}</span> : dirty ? "Unsaved changes" : ""}
          </span>
          <button type="button" className="secondary" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" disabled={!dirty || missingModel || submitting}>
            {submitting ? "Saving..." : submitLabel}
          </button>
        </footer>
      </form>
    </div>
  );
}
