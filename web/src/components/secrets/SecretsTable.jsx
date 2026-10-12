"use client";

import { useState } from "react";
import { useDispatch } from "react-redux";
import { deleteSecret, updateSecret } from "@/store/secretsSlice";

function SecretRow({ secret }) {
  const dispatch = useDispatch();
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);

  async function handleSave(e) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await dispatch(updateSecret({ key: secret.key, value })).unwrap();
      setEditing(false);
      setValue("");
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete() {
    if (!window.confirm(`Delete secret "${secret.key}"? This cannot be undone.`)) return;
    await dispatch(deleteSecret({ key: secret.key }));
  }

  return (
    <tr>
      <td className="secret-key-cell">{secret.key}</td>
      <td>
        {editing ? (
          <form className="secret-edit-form" onSubmit={handleSave}>
            <input
              type="password"
              autoFocus
              placeholder="New value"
              value={value}
              onChange={(e) => setValue(e.target.value)}
              required
            />
            <button type="submit" disabled={saving}>
              {saving ? "Saving..." : "Save"}
            </button>
            <button
              type="button"
              onClick={() => {
                setEditing(false);
                setValue("");
                setError(null);
              }}
            >
              Cancel
            </button>
            {error && <span className="error-text">{error}</span>}
          </form>
        ) : (
          <span className="muted">{secret.hasValue ? "•••••••• (set)" : "not set"}</span>
        )}
      </td>
      <td className="muted">{new Date(secret.updatedAt).toLocaleString()}</td>
      <td>
        {!editing && (
          <div className="secret-row-actions">
            <button type="button" onClick={() => setEditing(true)}>
              Edit
            </button>
            <button type="button" onClick={handleDelete}>
              Delete
            </button>
          </div>
        )}
      </td>
    </tr>
  );
}

export default function SecretsTable({ secrets }) {
  if (secrets.length === 0) return <p className="muted">No secrets yet. Add one below.</p>;

  return (
    <table className="secrets-table">
      <thead>
        <tr>
          <th>Key</th>
          <th>Value</th>
          <th>Updated</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        {secrets.map((secret) => (
          <SecretRow key={secret.key} secret={secret} />
        ))}
      </tbody>
    </table>
  );
}
