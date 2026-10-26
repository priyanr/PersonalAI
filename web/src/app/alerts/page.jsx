"use client";

import { useState } from "react";
import { useAlerts } from "@/hooks/useAlerts";
import AlertList from "@/components/alerts/AlertList";

export default function AlertsPage() {
  const [tab, setTab] = useState("attention");
  // Firing alerts load once and are split into "needs attention" and "acknowledged"; resolved history only when asked for.
  const active = useAlerts({ state: "active" });
  const resolved = useAlerts({ state: "resolved", enabled: tab === "resolved" });

  const needsAttention = (active.alerts ?? []).filter((a) => !a.acknowledged);
  const acknowledged = (active.alerts ?? []).filter((a) => a.acknowledged);
  const loaded = active.alerts !== null;

  const tabs = [
    { key: "attention", label: "Needs attention", count: loaded ? needsAttention.length : null },
    { key: "acknowledged", label: "Acknowledged", count: loaded ? acknowledged.length : null },
    { key: "resolved", label: "Resolved (7 days)", count: null }
  ];

  const attentionEmpty =
    acknowledged.length > 0
      ? `Nothing needs attention. ${acknowledged.length} alert${acknowledged.length === 1 ? " is" : "s are"} acknowledged and still firing.`
      : "Nothing is firing.";

  return (
    <main>
      <h1>Alerts</h1>
      <p className="muted">
        Raised automatically by the alert rules of your Dashboard &amp; Alerts agents after each collection pass, and cleared
        automatically when the condition stops holding. Acknowledging marks an alert as seen: it moves out of &quot;Needs
        attention&quot; and the menu count, stays listed while it is still firing, and needs attention again if it resolves and
        then comes back.
      </p>
      <div className="settings-tabs" role="tablist">
        {tabs.map((t) => (
          <button
            key={t.key}
            type="button"
            role="tab"
            aria-selected={tab === t.key}
            className={tab === t.key ? "active" : ""}
            onClick={() => setTab(t.key)}
          >
            {t.label}
            {t.count !== null && <span className="tab-count"> ({t.count})</span>}
          </button>
        ))}
      </div>

      {tab === "attention" && (
        <>
          {active.error && <p className="error-text">{active.error}</p>}
          {needsAttention.length > 1 && (
            <div className="alert-toolbar">
              <button type="button" className="secondary" onClick={active.acknowledgeAll}>
                Acknowledge all ({needsAttention.length})
              </button>
            </div>
          )}
          <AlertList
            alerts={active.alerts === null ? null : needsAttention}
            onAcknowledge={active.acknowledge}
            onUnacknowledge={active.unacknowledge}
            empty={attentionEmpty}
          />
        </>
      )}

      {tab === "acknowledged" && (
        <>
          {active.error && <p className="error-text">{active.error}</p>}
          <AlertList
            alerts={active.alerts === null ? null : acknowledged}
            onAcknowledge={active.acknowledge}
            onUnacknowledge={active.unacknowledge}
            empty="No acknowledged alerts. Acknowledged alerts stay here until the condition clears."
          />
        </>
      )}

      {tab === "resolved" && (
        <>
          {resolved.error && <p className="error-text">{resolved.error}</p>}
          <AlertList alerts={resolved.alerts} empty="Nothing resolved in the last 7 days." />
        </>
      )}
    </main>
  );
}
