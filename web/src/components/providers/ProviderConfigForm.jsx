"use client";

import { useEffect, useState } from "react";
import { useDispatch } from "react-redux";
import { updateProviderConfig } from "@/store/providersSlice";

function inputTypeFor(fieldType) {
  if (fieldType === "Secret") return "password";
  if (fieldType === "Number") return "number";
  return "text";
}

export default function ProviderConfigForm({ provider }) {
  const dispatch = useDispatch();
  const [values, setValues] = useState({});
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState(null);

  useEffect(() => {
    setValues(Object.fromEntries(provider.fields.map((f) => [f.name, f.type === "Secret" ? "" : (f.value ?? "")])));
    setSaved(false);
  }, [provider]);

  function handleChange(name, value) {
    setValues((prev) => ({ ...prev, [name]: value }));
    setSaved(false);
  }

  async function handleSubmit(e) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await dispatch(updateProviderConfig({ key: provider.key, values })).unwrap();
      setSaved(true);
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="provider-config-form" onSubmit={handleSubmit}>
      {provider.fields.map((field) => (
        <label key={field.name}>
          {field.label}
          {field.required && " *"}
          {field.type === "Boolean" ? (
            <input
              type="checkbox"
              checked={Boolean(values[field.name])}
              onChange={(e) => handleChange(field.name, e.target.checked)}
            />
          ) : (
            <input
              type={inputTypeFor(field.type)}
              value={values[field.name] ?? ""}
              placeholder={field.type === "Secret" && field.hasValue ? "•••••••• (unchanged — leave blank to keep)" : ""}
              onChange={(e) => handleChange(field.name, e.target.value)}
            />
          )}
        </label>
      ))}
      <div className="app-settings-actions">
        <button type="submit" disabled={saving}>
          {saving ? "Saving..." : "Save"}
        </button>
        {saved && !error && <span className="muted">Saved.</span>}
      </div>
      {error && <p className="error-text">{error}</p>}
    </form>
  );
}
