"use client";

import "../dashboard/dashboard.css";
import { useCallback, useEffect, useState } from "react";
import { apiDelete, apiGet, apiPatch, apiPost } from "@/lib/api";

const OPERATORS = [
  { value: "gt", label: ">" },
  { value: "gte", label: "≥" },
  { value: "lt", label: "<" },
  { value: "lte", label: "≤" },
  { value: "eq", label: "=" },
  { value: "ne", label: "≠" }
];

const SYMBOL = Object.fromEntries(OPERATORS.map((o) => [o.value, o.label]));

// One-click starting points; every field stays editable.
const PRESETS = [
  { label: "Data source down", rule: { name: "Data source down", dataset: "data_source_status", field: "available", operator: "eq", threshold: 0, forMinutes: 0, severity: "critical" } }
];

const EMPTY = { name: "", dataset: "", field: "", operator: "gte", threshold: "", forMinutes: 0, severity: "warning", target: "", filterKey: "", filterValue: "", enabled: true };

function describe(rule) {
  const parts = [`${rule.dataset}.${rule.field} ${SYMBOL[rule.operator] ?? rule.operator} ${rule.threshold}`];
  if (rule.forMinutes > 0) parts.push(`for ${rule.forMinutes} min`);
  if (rule.target) parts.push(`server ${rule.target}`);
  const filters = Object.entries(rule.filter ?? {});
  if (filters.length) parts.push(`where ${filters.map(([k, v]) => `${k}=${v}`).join(", ")}`);
  return parts.join(", ");
}

function RuleForm({ initial, agentId, onDone }) {
  const [form, setForm] = useState(initial);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);
  const set = (key, value) => setForm((f) => ({ ...f, [key]: value }));

  // A rule written by the model may carry several filters; the form edits one, so keep the rest untouched.
  const extraFilters = Object.entries(initial.filter ?? {}).slice(1);

  async function submit(e) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    const filter = Object.fromEntries([
      ...(form.filterKey.trim() ? [[form.filterKey.trim(), form.filterValue]] : []),
      ...extraFilters
    ]);
    const body = {
      name: form.name,
      dataset: form.dataset,
      field: form.field,
      operator: form.operator,
      threshold: form.threshold === "" ? null : Number(form.threshold),
      forMinutes: Number(form.forMinutes) || 0,
      severity: form.severity,
      target: form.target.trim() || null,
      filter: Object.keys(filter).length ? filter : null,
      enabled: form.enabled
    };
    try {
      if (initial.id) await apiPatch(`/api/agents/${agentId}/alert-rules/${initial.id}`, body);
      else await apiPost(`/api/agents/${agentId}/alert-rules`, body);
      onDone(true);
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="skill-form" onSubmit={submit}>
      <div className="skill-form-row">
        <label>
          Name
          <input type="text" value={form.name} onChange={(e) => set("name", e.target.value)} required />
        </label>
        <label>
          Dataset
          <input type="text" list="alert-datasets" value={form.dataset} onChange={(e) => set("dataset", e.target.value)} required />
          <datalist id="alert-datasets">
            {["data_source_status"].map((d) => (
              <option key={d} value={d} />
            ))}
          </datalist>
        </label>
        <label>
          Field
          <input type="text" placeholder="a field of the dataset" value={form.field} onChange={(e) => set("field", e.target.value)} required />
        </label>
      </div>
      <div className="skill-form-row">
        <label>
          Fires when value
          <select value={form.operator} onChange={(e) => set("operator", e.target.value)}>
            {OPERATORS.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </select>
        </label>
        <label>
          Threshold
          <input type="number" step="any" value={form.threshold} onChange={(e) => set("threshold", e.target.value)} required />
        </label>
        <label>
          For (minutes)
          <input type="number" min={0} max={1440} value={form.forMinutes} onChange={(e) => set("forMinutes", e.target.value)} />
        </label>
        <label>
          Severity
          <select value={form.severity} onChange={(e) => set("severity", e.target.value)}>
            <option value="warning">Warning</option>
            <option value="critical">Critical</option>
          </select>
        </label>
      </div>
      <div className="skill-form-row">
        <label>
          Only connection (optional)
          <input type="text" value={form.target} onChange={(e) => set("target", e.target.value)} />
        </label>
        <label>
          Only where field (optional)
          <input type="text" placeholder="mount" value={form.filterKey} onChange={(e) => set("filterKey", e.target.value)} />
        </label>
        <label>
          equals
          <input type="text" placeholder="/" value={form.filterValue} onChange={(e) => set("filterValue", e.target.value)} />
        </label>
        <label className="settings-checkbox">
          <input type="checkbox" checked={form.enabled} onChange={(e) => set("enabled", e.target.checked)} />
          Enabled
        </label>
      </div>
      <p className="muted">
        Booleans read as 1 (true) / 0 (false), so &quot;data_source_status.available = 0&quot; alerts on a server or database that can&apos;t be reached. &quot;For&quot; is how long the
        condition must hold before it fires; use it for noisy values. Checked against collected data when saved.
      </p>
      <div className="secret-row-actions">
        <button type="submit" disabled={saving}>
          {saving ? "Saving..." : initial.id ? "Save rule" : "Add rule"}
        </button>
        <button type="button" className="secondary" onClick={() => onDone(false)}>
          Cancel
        </button>
      </div>
      {error && <p className="error-text">{error}</p>}
    </form>
  );
}

export default function AlertRulesPanel({ agentId }) {
  const [rules, setRules] = useState(null);
  const [editing, setEditing] = useState(null); // a form object, or null
  const [error, setError] = useState(null);

  const load = useCallback(async () => {
    try {
      setRules(await apiGet(`/api/agents/${agentId}/alert-rules`));
    } catch (err) {
      setError(err.message);
    }
  }, [agentId]);

  useEffect(() => {
    load();
  }, [load]);

  function startEdit(rule) {
    const [key, value] = Object.entries(rule.filter ?? {})[0] ?? ["", ""];
    setEditing({ ...rule, threshold: String(rule.threshold), target: rule.target ?? "", filterKey: key, filterValue: value });
  }

  async function toggle(rule) {
    try {
      await apiPatch(`/api/agents/${agentId}/alert-rules/${rule.id}`, { ...rule, enabled: !rule.enabled });
      await load();
    } catch (err) {
      setError(err.message);
    }
  }

  async function remove(rule) {
    if (!window.confirm(`Delete rule "${rule.name}" and its alert history?`)) return;
    try {
      await apiDelete(`/api/agents/${agentId}/alert-rules/${rule.id}`);
      await load();
    } catch (err) {
      setError(err.message);
    }
  }

  return (
    <section className="agent-data-panel">
      <h2>Alert rules</h2>
      <p className="muted">
        Evaluated automatically after every collection pass; firing alerts appear on the Alerts page and this agent&apos;s
        dashboard. The model can add its own rules (marked &quot;model&quot;) but never changes yours.
      </p>
      {error && <p className="error-text">{error}</p>}

      {rules && rules.length > 0 && (
        <div className="dash-table-wrap">
          <table className="dash-table rule-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Condition</th>
                <th>Severity</th>
                <th>By</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {rules.map((rule) => (
                <tr key={rule.id}>
                  <td>
                    {rule.name}
                    {!rule.enabled && <span className="muted"> (disabled)</span>}
                  </td>
                  <td className="muted">{describe(rule)}</td>
                  <td>{rule.severity}</td>
                  <td className="muted">{rule.source === "llm" ? "model" : "you"}</td>
                  <td>
                    <div className="secret-row-actions">
                      <button type="button" className="secondary" onClick={() => startEdit(rule)}>
                        Edit
                      </button>
                      <button type="button" className="secondary" onClick={() => toggle(rule)}>
                        {rule.enabled ? "Disable" : "Enable"}
                      </button>
                      <button type="button" className="secondary" onClick={() => remove(rule)}>
                        Delete
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {rules && rules.length === 0 && !editing && <p className="muted">No rules yet. Start from a preset or run the agent to have the model suggest some.</p>}

      {editing ? (
        <RuleForm
          key={editing.id ?? "new"}
          initial={editing}
          agentId={agentId}
          onDone={(saved) => {
            setEditing(null);
            if (saved) load();
          }}
        />
      ) : (
        <>
          <div className="rule-presets">
            {PRESETS.map((p) => (
              <button key={p.label} type="button" className="secondary" onClick={() => setEditing({ ...EMPTY, ...p.rule, threshold: String(p.rule.threshold) })}>
                + {p.label}
              </button>
            ))}
            <button type="button" onClick={() => setEditing({ ...EMPTY })}>
              + Custom rule
            </button>
          </div>
        </>
      )}
    </section>
  );
}
