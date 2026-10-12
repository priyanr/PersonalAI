"use client";

import { useCallback, useEffect, useState } from "react";
import { apiGet } from "@/lib/api";

// The sandbox tool is useless until the PersonalAI.SandboxHost broker has dialled in to
// /api/sandbox/host, and that connection is established from the Docker host outwards -- there is
// nothing to click here. So unlike GmailConnectButton (which starts an OAuth flow), this custom
// field is purely a read-out of whether the broker is currently connected, which is the first thing
// to check when the tool starts failing.
// Mirrors SandboxTool.RenderPorts' own bracketing on the backend -- an unbracketed IPv6 address's own
// colons would be indistinguishable from a URL's port separator.
function urlHost(ip) {
  return ip.includes(":") ? `[${ip}]` : ip;
}

export default function SandboxHostStatus({ tool, field }) {
  const [status, setStatus] = useState(null);
  const [error, setError] = useState(null);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setStatus(await apiGet("/api/sandbox/status"));
      setError(null);
    } catch (err) {
      setError(err.message);
      setStatus(null);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    refresh();
  }, [refresh]);

  const connected = status?.connected ?? tool.isConnected;

  return (
    <div className="tool-custom-field">
      <div className="tool-custom-field-header">
        <span>{field.label}</span>
        {!loading && (
          <span className={`provider-badge ${connected ? "provider-badge-ok" : ""}`}>
            {connected ? "Connected" : "Not connected"}
          </span>
        )}
      </div>
      <div className="app-settings-actions">
        <button type="button" onClick={refresh} disabled={loading}>
          {loading ? "Checking..." : "Refresh"}
        </button>
      </div>
      {connected && status && (
        <p className="muted">
          Host <code>{status.hostId}</code>
          {status.remoteIp && (
            <>
              {" "}
              at <code>{status.remoteIp}</code>
            </>
          )}{" "}
          since {status.connectedAt ? new Date(status.connectedAt).toLocaleString() : "unknown"} &middot;{" "}
          {status.activeSandboxes} active sandbox{status.activeSandboxes === 1 ? "" : "es"}
          {status.remoteIp && (
            <>
              . Published ports are reachable at{" "}
              <code>{`http://${urlHost(status.remoteIp)}:<port>`}</code> — the sandbox tool tells the
              model the same address.
            </>
          )}
        </p>
      )}
      {!loading && !connected && !error && (
        <p className="muted">
          No sandbox host is connected. Start PersonalAI.SandboxHost on the Docker host with a matching{" "}
          <code>Sandbox:HostKey</code>; it dials in to this API, not the other way around.
        </p>
      )}
      {error && <p className="error-text">{error}</p>}
    </div>
  );
}
