"use client";

import { useState } from "react";
import { useDispatch } from "react-redux";
import {
  deleteServer,
  deleteServerGroup,
  resetServerHostKey,
  testServerConnection,
  updateServerGroup
} from "@/store/sshServersSlice";
import SshServerForm from "./SshServerForm";

function ServerRow({ server, onEdit }) {
  const dispatch = useDispatch();
  const [testing, setTesting] = useState(false);
  const [message, setMessage] = useState(null);

  async function handleTest() {
    setTesting(true);
    setMessage(null);
    try {
      const { result } = await dispatch(testServerConnection({ id: server.id })).unwrap();
      setMessage(result.success ? { ok: true, text: result.serverInfo || "Connected" } : { ok: false, text: result.error });
    } catch (err) {
      setMessage({ ok: false, text: err.message });
    } finally {
      setTesting(false);
    }
  }

  async function handleDelete() {
    if (!window.confirm(`Delete server "${server.name}"?`)) return;
    await dispatch(deleteServer({ id: server.id }));
  }

  async function handleResetKey() {
    if (!window.confirm("Forget the pinned host key? The next successful connection will pin whatever key the server presents.")) return;
    await dispatch(resetServerHostKey({ id: server.id }));
  }

  const status = server.lastError
    ? { cls: "error-text", text: server.lastError }
    : server.lastSeenAt
      ? { cls: "muted", text: `seen ${new Date(server.lastSeenAt).toLocaleString()}` }
      : { cls: "muted", text: "never connected" };

  return (
    <tr>
      <td>
        {server.name}
        {!server.enabled && <span className="muted"> (disabled)</span>}
      </td>
      <td className="muted">
        {server.username}@{server.address}:{server.port}
      </td>
      <td className="muted">{server.secretRef}</td>
      <td>
        <span className={status.cls}>{status.text}</span>
        {server.hostKeyFingerprint && (
          <div className="muted" title={server.hostKeyFingerprint}>
            host key pinned
          </div>
        )}
        {message && <div className={message.ok ? "muted" : "error-text"}>{message.ok ? `OK: ${message.text}` : message.text}</div>}
      </td>
      <td>
        <div className="secret-row-actions">
          <button type="button" onClick={handleTest} disabled={testing}>
            {testing ? "Testing..." : "Test"}
          </button>
          <button type="button" onClick={() => onEdit(server)}>
            Edit
          </button>
          {server.hostKeyFingerprint && (
            <button type="button" onClick={handleResetKey}>
              Reset key
            </button>
          )}
          <button type="button" onClick={handleDelete}>
            Delete
          </button>
        </div>
      </td>
    </tr>
  );
}

export default function SshServerGroupSection({ group, servers, groups }) {
  const dispatch = useDispatch();
  const [editingServer, setEditingServer] = useState(null);
  const [adding, setAdding] = useState(false);

  async function handleRename() {
    const name = window.prompt("Group name", group.name);
    if (!name || name.trim() === group.name) return;
    try {
      await dispatch(updateServerGroup({ id: group.id, name: name.trim(), description: group.description })).unwrap();
    } catch (err) {
      window.alert(err.message);
    }
  }

  async function handleDelete() {
    const n = servers.length;
    const detail = n > 0 ? ` and its ${n} server(s)` : "";
    if (!window.confirm(`Delete group "${group.name}"${detail}?`)) return;
    await dispatch(deleteServerGroup({ id: group.id }));
  }

  return (
    <section className="server-group">
      <h2>
        {group.name} <span className="muted">({servers.length})</span>
      </h2>
      {group.description && <p className="muted">{group.description}</p>}
      <div className="secret-row-actions">
        <button type="button" onClick={handleRename}>
          Rename
        </button>
        <button type="button" onClick={handleDelete}>
          Delete group
        </button>
      </div>

      {servers.length === 0 ? (
        <p className="muted">No servers in this group yet.</p>
      ) : (
        <table className="secrets-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Endpoint</th>
              <th>Credential</th>
              <th>Status</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {servers.map((t) =>
              editingServer?.id === t.id ? (
                <tr key={t.id}>
                  <td colSpan={5}>
                    <SshServerForm
                      groups={groups}
                      initial={t}
                      onDone={() => setEditingServer(null)}
                    />
                  </td>
                </tr>
              ) : (
                <ServerRow key={t.id} server={t} onEdit={setEditingServer} />
              )
            )}
          </tbody>
        </table>
      )}

      {adding ? (
        <SshServerForm groups={groups} defaultGroupId={group.id} onDone={() => setAdding(false)} />
      ) : (
        <button type="button" onClick={() => setAdding(true)}>
          Add server
        </button>
      )}
    </section>
  );
}
