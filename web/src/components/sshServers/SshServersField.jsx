"use client";

import { useState } from "react";
import { useDispatch } from "react-redux";
import { useSshServers } from "@/hooks/useSshServers";
import { createServerGroup } from "@/store/sshServersSlice";
import SshServerGroupSection from "./SshServerGroupSection";

function AddGroupForm() {
  const dispatch = useDispatch();
  const [name, setName] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);

  async function handleSubmit(e) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await dispatch(createServerGroup({ name: name.trim(), description: null })).unwrap();
      setName("");
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="secret-add-form" onSubmit={handleSubmit}>
      <input placeholder="New group name (e.g. Production)" value={name} onChange={(e) => setName(e.target.value)} required />
      <button type="submit" disabled={saving}>
        {saving ? "Adding..." : "Add group"}
      </button>
      {error && <p className="error-text">{error}</p>}
    </form>
  );
}

// The SSH tool's "Servers" setting: the groups and servers that Dashboard & Alerts agents connect to. Rendered inside
// the SSH tool card on Settings > Tools through the custom-field registry.
export default function SshServersField() {
  const { groups, servers, status, error } = useSshServers();

  return (
    <div className="ssh-servers">
      <h3>Servers</h3>
      <p className="muted">
        Groups of servers that agents monitor over SSH. A server&apos;s key or password comes from a secret named{" "}
        <code>SSH_</code>something on the Secrets page. Agents run only the commands allowed above, and the server&apos;s
        host key is pinned on the first successful connection.
      </p>
      {status === "loading" && <p className="muted">Loading...</p>}
      {status === "failed" && <p className="error-text">{error}</p>}
      {status === "succeeded" && groups.length === 0 && <p className="muted">No groups yet. Add one below.</p>}
      {groups.map((group) => (
        <SshServerGroupSection
          key={group.id}
          group={group}
          groups={groups}
          servers={servers.filter((t) => t.groupId === group.id)}
        />
      ))}
      <AddGroupForm />
    </div>
  );
}
