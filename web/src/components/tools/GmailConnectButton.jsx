"use client";

import { useEffect, useState } from "react";
import { useDispatch } from "react-redux";
import { useRouter, useSearchParams } from "next/navigation";
import { apiGet } from "@/lib/api";
import { fetchTools } from "@/store/toolsSlice";

export default function GmailConnectButton({ tool, field }) {
  const dispatch = useDispatch();
  const router = useRouter();
  const searchParams = useSearchParams();
  const [redirectUri, setRedirectUri] = useState(null);
  const [connecting, setConnecting] = useState(false);
  const [connectError, setConnectError] = useState(null);
  const [statusMessage, setStatusMessage] = useState(null);
  const [statusIsError, setStatusIsError] = useState(false);

  useEffect(() => {
    apiGet("/api/tools/gmail/oauth/redirect-uri")
      .then((data) => setRedirectUri(data.redirectUri))
      .catch(() => {
        // Non-fatal -- just omit the hint if this call fails.
      });
  }, []);

  useEffect(() => {
    const connected = searchParams.get("connected");
    const error = searchParams.get("error");
    if (connected !== "gmail") return;

    dispatch(fetchTools());
    setStatusIsError(Boolean(error));
    setStatusMessage(error ? `Gmail connection failed: ${error}` : "Gmail connected successfully.");
    router.replace("/settings/tools");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams]);

  async function handleConnect() {
    setConnecting(true);
    setConnectError(null);
    try {
      const { url } = await apiGet("/api/tools/gmail/oauth/authorize-url");
      window.location.href = url;
    } catch (err) {
      setConnectError(err.message);
      setConnecting(false);
    }
  }

  return (
    <div className="tool-custom-field">
      <div className="tool-custom-field-header">
        <span>{field.label}</span>
        {tool.isConnected && <span className="provider-badge provider-badge-ok">Connected</span>}
      </div>
      <div className="app-settings-actions">
        <button type="button" onClick={handleConnect} disabled={!tool.isConfigured || connecting}>
          {connecting ? "Connecting..." : "Connect with Google"}
        </button>
      </div>
      {!tool.isConfigured && (
        <p className="muted">Save the Client ID and Client Secret above before connecting.</p>
      )}
      {statusMessage && <p className={statusIsError ? "error-text" : "muted"}>{statusMessage}</p>}
      {connectError && <p className="error-text">{connectError}</p>}
      {redirectUri && (
        <p className="muted">
          Register this redirect URI in your Google Cloud OAuth Client: <code>{redirectUri}</code>
        </p>
      )}
    </div>
  );
}
