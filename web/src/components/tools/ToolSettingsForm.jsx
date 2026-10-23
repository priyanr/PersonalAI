"use client";

import { useEffect, useState } from "react";
import { useDispatch } from "react-redux";
import { updateToolConfig } from "@/store/toolsSlice";
import { customToolFields } from "./customToolFields";

function inputTypeFor(fieldType) {
  if (fieldType === "Secret") return "password";
  if (fieldType === "Number") return "number";
  if (fieldType === "Url") return "url";
  return "text";
}

export default function ToolSettingsForm({ tool }) {
  const dispatch = useDispatch();
  const [values, setValues] = useState({});
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState(null);

  const genericFields = tool.fields.filter((f) => f.type !== "Custom");
  const customFields = tool.fields.filter((f) => f.type === "Custom");

  useEffect(() => {
    setValues(Object.fromEntries(genericFields.map((f) => [f.name, f.type === "Secret" ? "" : (f.value ?? "")])));
    setSaved(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tool]);

  function handleChange(name, value) {
    setValues((prev) => ({ ...prev, [name]: value }));
    setSaved(false);
  }

  async function handleSubmit(e) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await dispatch(updateToolConfig({ name: tool.name, values })).unwrap();
      setSaved(true);
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="tool-settings-form">
      {genericFields.length > 0 && (
        <form className="provider-config-form" onSubmit={handleSubmit}>
          {genericFields.map((field) => (
            <label key={field.name}>
              {field.label}
              {field.required && " *"}
              {field.type === "Boolean" ? (
                <input
                  type="checkbox"
                  checked={Boolean(values[field.name])}
                  onChange={(e) => handleChange(field.name, e.target.checked)}
                />
              ) : field.type === "TextArea" ? (
                <textarea
                  rows={8}
                  className="tool-textarea"
                  value={values[field.name] ?? ""}
                  placeholder={field.placeholder ?? ""}
                  onChange={(e) => handleChange(field.name, e.target.value)}
                />
              ) : (
                <input
                  type={inputTypeFor(field.type)}
                  value={values[field.name] ?? ""}
                  placeholder={
                    field.type === "Secret" && field.hasValue
                      ? "•••••••• (unchanged — leave blank to keep)"
                      : (field.placeholder ?? "")
                  }
                  {...(field.type === "Number" ? { min: 1, step: 1 } : {})}
                  onChange={(e) => handleChange(field.name, e.target.value)}
                />
              )}
              {field.help && <span className="field-help muted">{field.help}</span>}
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
      )}
      {customFields.map((field) => {
        const CustomComponent = customToolFields[field.customComponentKey];
        if (!CustomComponent) return null;
        return <CustomComponent key={field.name} tool={tool} field={field} />;
      })}
    </div>
  );
}
