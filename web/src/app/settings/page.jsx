"use client";

import { useEffect, useState } from "react";
import { apiGet, apiPatch } from "@/lib/api";
import SettingsTabs from "@/components/layout/SettingsTabs";

export default function SettingsPage() {
  const [webSearchGuidance, setWebSearchGuidance] = useState("");
  const [isDefault, setIsDefault] = useState(true);
  const [thinkingEnabled, setThinkingEnabled] = useState(true);
  const [status, setStatus] = useState("loading");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null);

  useEffect(() => {
    apiGet("/api/app-settings")
      .then((data) => {
        setWebSearchGuidance(data.webSearchGuidance);
        setIsDefault(data.isDefault);
        setThinkingEnabled(data.thinkingEnabled);
        setStatus("succeeded");
      })
      .catch((err) => {
        setError(err.message);
        setStatus("failed");
      });
  }, []);

  async function handleSave() {
    setSaving(true);
    setError(null);
    try {
      const data = await apiPatch("/api/app-settings", { webSearchGuidance, thinkingEnabled });
      setWebSearchGuidance(data.webSearchGuidance);
      setIsDefault(data.isDefault);
      setThinkingEnabled(data.thinkingEnabled);
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }

  async function handleResetToDefault() {
    setSaving(true);
    setError(null);
    try {
      const data = await apiPatch("/api/app-settings", { webSearchGuidance: null, thinkingEnabled });
      setWebSearchGuidance(data.webSearchGuidance);
      setIsDefault(data.isDefault);
      setThinkingEnabled(data.thinkingEnabled);
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <main>
      <SettingsTabs />
      <h1>App settings</h1>
      {status === "loading" && <p className="muted">Loading...</p>}
      {status === "succeeded" && (
        <div className="app-settings-form">
          <label className="settings-checkbox">
            <input
              type="checkbox"
              checked={thinkingEnabled}
              onChange={(e) => setThinkingEnabled(e.target.checked)}
            />
            Enable model thinking by default
          </label>
          <p className="muted">
            Default for sessions that don&apos;t explicitly override it. Some models spend a long time on
            hidden reasoning before answering — turn this off if you&apos;d rather they answer directly.
          </p>
          <label>
            Web search guidance
            <p className="muted">
              Appended to the web_search tool&apos;s description sent to every model, on every session
              where web search is enabled. Use it to steer how aggressively the model retries searches.
            </p>
            <textarea
              rows={6}
              value={webSearchGuidance}
              onChange={(e) => setWebSearchGuidance(e.target.value)}
            />
          </label>
          <div className="app-settings-actions">
            <button type="button" onClick={handleSave} disabled={saving}>
              {saving ? "Saving..." : "Save"}
            </button>
            <button type="button" className="secondary" onClick={handleResetToDefault} disabled={saving || isDefault}>
              Reset to default
            </button>
          </div>
        </div>
      )}
      {error && <p className="error-text">{error}</p>}
    </main>
  );
}
