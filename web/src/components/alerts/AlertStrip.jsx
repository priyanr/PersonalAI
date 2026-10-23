"use client";

import { useAlerts } from "@/hooks/useAlerts";
import AlertList from "./AlertList";

// Active alerts for one agent, shown above its dashboard widgets whether or not the model added anything about
// alerts to the spec. Alerts nobody has seen come first and are what the heading counts; acknowledged ones stay
// available under a collapsed section. Renders nothing when all is quiet.
export default function AlertStrip({ agentId }) {
  const { alerts, acknowledge, unacknowledge, acknowledgeAll } = useAlerts({ state: "active", agentId });
  if (!alerts || alerts.length === 0) return null;

  const needsAttention = alerts.filter((a) => !a.acknowledged);
  const acknowledged = alerts.filter((a) => a.acknowledged);

  return (
    <section className="alert-strip" aria-label="Active alerts">
      <div className="alert-strip-head">
        <h3>
          {needsAttention.length > 0
            ? `${needsAttention.length} alert${needsAttention.length === 1 ? "" : "s"} need${needsAttention.length === 1 ? "s" : ""} attention`
            : "All active alerts are acknowledged"}
        </h3>
        {needsAttention.length > 1 && (
          <button type="button" className="secondary" onClick={acknowledgeAll}>
            Acknowledge all
          </button>
        )}
      </div>
      {needsAttention.length > 0 && (
        <AlertList alerts={needsAttention} onAcknowledge={acknowledge} onUnacknowledge={unacknowledge} showAgent={false} />
      )}
      {acknowledged.length > 0 && (
        <details className="alert-strip-acked" open={needsAttention.length === 0}>
          <summary className="muted">
            {acknowledged.length} acknowledged, still firing
          </summary>
          <AlertList alerts={acknowledged} onAcknowledge={acknowledge} onUnacknowledge={unacknowledge} showAgent={false} />
        </details>
      )}
    </section>
  );
}
