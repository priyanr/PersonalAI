"use client";

import { useState } from "react";
import { useDispatch } from "react-redux";
import Link from "next/link";
import { useSecrets } from "@/hooks/useSecrets";
import { createServer, updateServer } from "@/store/sshServersSlice";

export default function SshServerForm({ groups, initial, defaultGroupId, onDone }) {
  const dispatch = useDispatch();
  // Only secrets named SSH_* can be used by a server (enforced by the API); don't offer the rest.
  const { secrets: allSecrets } = useSecrets();
  const secrets = allSecrets.filter((s) => s.key.toUpperCase().startsWith("SSH_"));
  const [groupId, setGroupId] = useState(initial?.groupId ?? defaultGroupId ?? groups[0]?.id ?? "");
  const [name, setName] = useState(initial?.name ?? "");
  const [address, setAddress] = useState(initial?.address ?? "");
  const [port, setPort] = useState(String(initial?.port ?? 22));
  const [username, setUsername] = useState(initial?.username ?? "");
  const [secretRef, setSecretRef] = useState(initial?.secretRef ?? "");
  const [enabled, setEnabled] = useState(initial?.enabled ?? true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);

  async function handleSubmit(e) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    const body = {
      groupId,
      name: name.trim(),
      address: address.trim(),
      port: port ? Number(port) : null,
      username: username.trim(),
      secretRef,
      enabled
    };
    try {
      if (initial) await dispatch(updateServer({ id: initial.id, ...body })).unwrap();
      else await dispatch(createServer(body)).unwrap();
      onDone();
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="skill-form" onSubmit={handleSubmit}>
      <div className="skill-form-row">
        <label>
          Name
          <input type="text" value={name} onChange={(e) => setName(e.target.value)} required />
        </label>
        <label>
          Address
          <input
            type="text"
            placeholder="10.0.0.5 or host.example.com"
            value={address}
            onChange={(e) => setAddress(e.target.value)}
            required
          />
        </label>
        <label>
          Port
          <input type="number" min={1} max={65535} value={port} onChange={(e) => setPort(e.target.value)} />
        </label>
        <label>
          Username
          <input type="text" value={username} onChange={(e) => setUsername(e.target.value)} required />
        </label>
      </div>
      <div className="skill-form-row">
        <label>
          SSH key / password secret
          <select value={secretRef} onChange={(e) => setSecretRef(e.target.value)} required>
            <option value="">{secrets.length ? "Select a secret…" : "No SSH_ secrets yet"}</option>
            {secrets.map((s) => (
              <option key={s.key} value={s.key}>
                {s.key}
              </option>
            ))}
          </select>
        </label>
        {groups.length > 1 && (
          <label>
            Group
            <select value={groupId} onChange={(e) => setGroupId(e.target.value)}>
              {groups.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.name}
                </option>
              ))}
            </select>
          </label>
        )}
        <label className="settings-checkbox">
          <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
          Enabled
        </label>
      </div>
      <p className="muted">
        The secret&apos;s value is either a PEM private key or a password. Name it <code>SSH_</code>something (for example{" "}
        <code>SSH_HOMELAB_KEY</code>) on the <Link href="/settings/secrets">Secrets</Link> page first; only SSH_ secrets can be used here. The server&apos;s host key is pinned on the first
        successful connection.
      </p>
      <div className="secret-row-actions">
        <button type="submit" disabled={saving}>
          {saving ? "Saving..." : initial ? "Save" : "Add server"}
        </button>
        <button type="button" onClick={onDone}>
          Cancel
        </button>
      </div>
      {error && <p className="error-text">{error}</p>}
    </form>
  );
}
