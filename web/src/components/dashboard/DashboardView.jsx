"use client";

import "./dashboard.css";
import { useCallback, useEffect, useState } from "react";
import { apiGet, apiPost } from "@/lib/api";
import AgentRunPanel from "@/components/agents/AgentRunPanel";
import AlertStrip from "@/components/alerts/AlertStrip";
import WidgetCard from "./WidgetCard";

const DEFAULT_REFRESH_SECONDS = 30;

function versionLabel(v) {
  const when = new Date(v.createdAt).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit", hour12: false });
  return `v${v.version} · ${when}${v.reason ? ` · ${v.reason}` : ""}`;
}

export default function DashboardView({ agentId, agentName }) {
  const [versions, setVersions] = useState([]);
  const [selected, setSelected] = useState(null); // null = current
  const [dashboard, setDashboard] = useState(undefined); // undefined = loading, null = none yet
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [restoring, setRestoring] = useState(false);

  const query = selected === null ? "" : `?version=${selected}`;

  const loadVersions = useCallback(async () => {
    try {
      setVersions(await apiGet(`/api/agents/${agentId}/dashboard/versions`));
    } catch (err) {
      setError(err.message);
    }
  }, [agentId]);

  const loadDashboard = useCallback(async () => {
    try {
      const result = await apiGet(`/api/agents/${agentId}/dashboard${query}`);
      setDashboard(result ?? null);
      setError(null);
      return result;
    } catch (err) {
      setError(err.message);
      return null;
    }
  }, [agentId, query]);

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      setData(await apiGet(`/api/agents/${agentId}/dashboard/data${query}`));
      setError(null);
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }, [agentId, query]);

  useEffect(() => {
    loadVersions();
  }, [loadVersions]);

  useEffect(() => {
    let cancelled = false;
    loadDashboard().then((result) => {
      if (!cancelled && result) loadData();
    });
    return () => {
      cancelled = true;
    };
  }, [loadDashboard, loadData]);

  // Live: refetch data on the dashboard's own cadence. A past version is static, so it isn't polled.
  const refreshSeconds = dashboard?.spec?.refreshSeconds ?? DEFAULT_REFRESH_SECONDS;
  useEffect(() => {
    if (!dashboard || selected !== null) return undefined;
    const timer = setInterval(loadData, refreshSeconds * 1000);
    return () => clearInterval(timer);
  }, [dashboard, selected, refreshSeconds, loadData]);

  async function handleRestore() {
    if (!window.confirm(`Make v${selected} the current dashboard? It is saved as a new version; nothing is lost.`)) return;
    setRestoring(true);
    try {
      await apiPost(`/api/agents/${agentId}/dashboard/versions/${selected}/restore`, {});
      setSelected(null);
      await loadVersions();
    } catch (err) {
      setError(err.message);
    } finally {
      setRestoring(false);
    }
  }

  async function handleRunComplete() {
    setSelected(null);
    await loadVersions();
    const result = await loadDashboard();
    if (result) loadData();
  }

  const spec = dashboard?.spec;
  const current = versions[0]?.version;

  return (
    <div className="dash">
      {dashboard === undefined && !error && <p className="muted">Loading dashboard…</p>}
      {error && <p className="error-text">{error}</p>}

      {dashboard === null && (
        <p className="muted">
          No dashboard yet. Run the agent below: it will read the collected data and design one. Collect some data first
          (agent page → Collection) so it has something to work from.
        </p>
      )}

      {spec && (
        <>
          <div className="dash-head">
            {spec.title !== agentName ? <h2>{spec.title}</h2> : <span />}
            <span className="muted">
              {data ? `Data as of ${new Date(data.generatedAt).toLocaleTimeString()}` : ""}
              {selected === null && data ? ` · refreshes every ${refreshSeconds}s` : ""}
            </span>
          </div>
          {spec.description && <p className="muted">{spec.description}</p>}

          <div className="dash-toolbar">
            <label>
              Version
              <select value={selected ?? ""} onChange={(e) => setSelected(e.target.value === "" ? null : Number(e.target.value))}>
                <option value="">Current{current ? ` (v${current})` : ""}</option>
                {versions.slice(1).map((v) => (
                  <option key={v.version} value={v.version}>
                    {versionLabel(v)}
                  </option>
                ))}
              </select>
            </label>
            {selected !== null && (
              <>
                <span className="muted">Viewing an older version.</span>
                <button type="button" onClick={handleRestore} disabled={restoring}>
                  {restoring ? "Restoring…" : "Restore this version"}
                </button>
              </>
            )}
            {selected === null && (
              <button type="button" className="secondary" onClick={loadData} disabled={loading}>
                {loading ? "Refreshing…" : "Refresh now"}
              </button>
            )}
            {selected === null && dashboard.reason && <span className="muted">Last change: {dashboard.reason}</span>}
          </div>

          {selected === null && <AlertStrip agentId={agentId} />}

          <div className={`dash-grid${loading && data ? " is-loading" : ""}`}>
            {(spec.widgets ?? []).map((widget) => (
              <WidgetCard key={widget.id} widget={widget} data={data?.widgets?.[widget.id]} />
            ))}
          </div>
        </>
      )}

      <details className="dash-regenerate" open={dashboard === null}>
        <summary>{dashboard === null ? "Build the dashboard" : "Regenerate with AI"}</summary>
        <p className="muted">
          Runs the agent now: it reviews the collected data, refines the dashboard and saves a new version. This uses the
          model; the dashboard itself updates live without it.
        </p>
        <AgentRunPanel agentId={agentId} onRunComplete={handleRunComplete} />
      </details>
    </div>
  );
}
