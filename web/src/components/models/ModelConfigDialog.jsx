"use client";

import { useEffect, useState } from "react";
import { useDispatch } from "react-redux";
import { resetModelSettings, saveModelSettings } from "@/store/modelsSlice";

// Per-model capability settings (output-limit parameter, tool calling, reasoning effort...). Fields come from
// the provider, so this renders whatever it declares; blank / "auto" means the provider's own default.
export default function ModelConfigDialog({ providerKey, model, fields, onClose }) {
  const dispatch = useDispatch();
  const [values, setValues] = useState({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(null);

  useEffect(() => {
    const saved = model.settings ?? {};
    setValues(Object.fromEntries(fields.map((f) => [f.name, saved[f.name] ?? (f.type === "Select" ? f.defaultValue ?? "" : "")])));
  }, [model, fields]);

  useEffect(() => {
    function onKey(e) {
      if (e.key === "Escape") onClose();
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function run(action) {
    setBusy(true);
    setError(null);
    try {
      await dispatch(action).unwrap();
      onClose();
    } catch (err) {
      setError(err.message);
      setBusy(false);
    }
  }

  function handleSubmit(e) {
    e.preventDefault();
    run(saveModelSettings({ providerKey, model: model.name, values }));
  }

  return (
    <div className="model-config-overlay" role="dialog" aria-modal="true" aria-label={`Configure ${model.name}`} onClick={onClose}>
      <form className="model-config-dialog provider-config-form" onSubmit={handleSubmit} onClick={(e) => e.stopPropagation()}>
        <h2>
          Configure <code>{model.name}</code>
        </h2>
        {fields.length === 0 && <p className="muted">This provider has no per-model settings.</p>}
        {fields.map((field) => (
          <label key={field.name}>
            {field.label}
            {field.type === "Select" ? (
              <select value={values[field.name] ?? ""} onChange={(e) => setValues((v) => ({ ...v, [field.name]: e.target.value }))}>
                {field.options.map((option) => (
                  <option key={option} value={option}>
                    {option}
                  </option>
                ))}
              </select>
            ) : (
              <input
                type={field.type === "Number" ? "number" : "text"}
                min={field.type === "Number" ? 1 : undefined}
                value={values[field.name] ?? ""}
                placeholder="default"
                onChange={(e) => setValues((v) => ({ ...v, [field.name]: e.target.value }))}
              />
            )}
            {field.help && <span className="muted model-config-help">{field.help}</span>}
          </label>
        ))}
        {error && <p className="error-text">{error}</p>}
        <div className="app-settings-actions">
          <button type="submit" disabled={busy || fields.length === 0}>
            {busy ? "Saving..." : "Save"}
          </button>
          <button
            type="button"
            className="secondary"
            disabled={busy}
            onClick={() => run(resetModelSettings({ providerKey, model: model.name }))}
          >
            Reset to auto
          </button>
          <button type="button" className="secondary" disabled={busy} onClick={onClose}>
            Cancel
          </button>
        </div>
      </form>
    </div>
  );
}
