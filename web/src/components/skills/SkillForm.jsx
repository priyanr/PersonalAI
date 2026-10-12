"use client";

import { useState } from "react";
import { useModels } from "@/hooks/useModels";
import ModelSelect from "@/components/models/ModelSelect";
import { useProviders } from "@/hooks/useProviders";
import { useSecrets } from "@/hooks/useSecrets";
import { CONTEXT_SIZE_OPTIONS, SKILL_TOOL_OPTIONS } from "@/lib/enums";

function toggle(list, value) {
  return list.includes(value) ? list.filter((v) => v !== value) : [...list, value];
}

export default function SkillForm({ initial, onSubmit, submitLabel, submitting, error }) {
  const { providers } = useProviders();
  const { secrets } = useSecrets();
  const [name, setName] = useState(initial?.name ?? "");
  const [description, setDescription] = useState(initial?.description ?? "");
  const [prompt, setPrompt] = useState(initial?.prompt ?? "");
  const [providerKey, setProviderKey] = useState(initial?.providerKey ?? "ollama");
  const { models, status: modelsStatus } = useModels(providerKey);
  const [modelName, setModelName] = useState(initial?.modelName ?? "");
  const [numCtx, setNumCtx] = useState(initial?.numCtx ? String(initial.numCtx) : "");
  const [enabledTools, setEnabledTools] = useState(initial?.enabledTools ?? []);
  const [secretKeys, setSecretKeys] = useState(initial?.secretKeys ?? []);

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
      name: name.trim(),
      description: description.trim() || null,
      prompt,
      providerKey,
      modelName,
      numCtx: numCtx ? Number(numCtx) : null,
      enabledTools,
      secretKeys
    });
  }

  return (
    <form className="skill-form" onSubmit={handleSubmit}>
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
          placeholder="Instructions the skill runs with every time — its system prompt."
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
      </div>

      <fieldset className="skill-form-fieldset">
        <legend>Tools this skill may call</legend>
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
        <legend>Secrets this skill may read</legend>
        {secrets.length === 0 ? (
          <p className="muted">
            No secrets configured yet — add one on the Secrets page to make it available here.
          </p>
        ) : (
          <div className="tool-toggles-group">
            {secrets.map((s) => (
              <label key={s.key} className="settings-checkbox settings-checkbox-sub">
                <input
                  type="checkbox"
                  checked={secretKeys.includes(s.key)}
                  onChange={() => setSecretKeys((prev) => toggle(prev, s.key))}
                />
                {s.key}
              </label>
            ))}
          </div>
        )}
        {secretKeys.length > 0 && (
          <p className="muted">
            Reference these in tool call arguments as {secretKeys.map((k) => `{{secret.${k}}}`).join(", ")} —
            the real value is substituted in only when a tool runs; the model never sees it.
          </p>
        )}
      </fieldset>

      <button type="submit" disabled={submitting || !modelName}>
        {submitting ? "Saving..." : submitLabel}
      </button>
      {error && <p className="error-text">{error}</p>}
    </form>
  );
}
