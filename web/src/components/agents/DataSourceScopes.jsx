"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import "../dashboard/dashboard.css";
import { apiGet } from "@/lib/api";

function toggle(list, id) {
  return list.includes(id) ? list.filter((v) => v !== id) : [...list, id];
}

function plural(noun, n) {
  return `${n} ${noun}${n === 1 ? "" : "s"}`;
}

// What this agent may reach through each data tool. Nothing here knows about SSH or SQL: every tool describes its own
// scope items (a group of connections, or a single connection) through /api/data-sources, and the agent's settings
// hold `toolScopes: { <tool>: [scope item ids] }`. A tool with something selected is enabled for the agent.
export default function DataSourceScopes({ value, onChange }) {
  const [sources, setSources] = useState(null);
  const [error, setError] = useState(null);

  useEffect(() => {
    let cancelled = false;
    apiGet("/api/data-sources")
      .then((result) => !cancelled && setSources(result))
      .catch((err) => !cancelled && setError(err.message));
    return () => {
      cancelled = true;
    };
  }, []);

  function setScope(tool, ids) {
    const next = { ...value, [tool]: ids };
    if (ids.length === 0) delete next[tool];
    onChange(next);
  }

  return (
    <div className="data-source-scopes">
      <p className="muted">
        Choose what this agent may reach. A tool is available to the agent only if you select something under it, and
        the agent can use nothing outside your selection.
      </p>
      {error && <p className="error-text">{error}</p>}
      {sources === null && !error && <p className="muted">Loading…</p>}
      {sources?.length === 0 && <p className="muted">No data tools are available.</p>}

      {sources?.map((source) => {
        const selected = value?.[source.tool] ?? [];
        return (
          <div key={source.tool} className="data-source">
            <div className="data-source-head">
              <strong>{source.displayName}</strong>
              <span className="muted">
                {selected.length > 0 ? `enabled for this agent` : `not enabled`} ·{" "}
                <Link href={source.settingsPath}>manage {source.connectionNoun}s</Link>
              </span>
            </div>
            {source.items.length === 0 ? (
              <p className="muted">
                No {source.connectionNoun}s configured yet. Add them in <Link href={source.settingsPath}>Settings → Tools → {source.displayName}</Link>.
              </p>
            ) : (
              <div className="tool-toggles-group">
                {source.items.map((item) => (
                  <label key={item.id} className="settings-checkbox settings-checkbox-sub">
                    <input
                      type="checkbox"
                      checked={selected.includes(item.id)}
                      onChange={() => setScope(source.tool, toggle(selected, item.id))}
                    />
                    <span>
                      {item.name}
                      <span className="muted">
                        {item.kind === "group"
                          ? ` (${plural(source.connectionNoun, item.connections.length)}${item.connections.length > 0 ? `: ${item.connections.join(", ")}` : ""})`
                          : ""}
                      </span>
                    </span>
                  </label>
                ))}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
