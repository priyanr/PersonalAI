"use client";

import "../dashboard/dashboard.css";
import Link from "next/link";
import StatusBadge from "../dashboard/StatusBadge";

function since(iso) {
  if (!iso) return "";
  const minutes = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h ago`;
  return `${Math.round(hours / 24)} d ago`;
}

// Resolved alerts are shown as "ok"; firing ones take their rule's severity. Always icon + word, never colour alone.
function statusFor(alert) {
  if (alert.state === "resolved") return "ok";
  return alert.severity === "critical" ? "critical" : "warning";
}

// A firing alert is either waiting for someone (Acknowledge) or already seen (Undo). Resolving is automatic: once the
// condition clears on a later collection pass the alert moves to Resolved by itself, acknowledged or not.
export default function AlertList({ alerts, onAcknowledge, onUnacknowledge, showAgent = true, empty = "No alerts." }) {
  if (!alerts) return <p className="muted">Loading…</p>;
  if (alerts.length === 0) return <p className="muted">{empty}</p>;

  return (
    <div className="dash">
      <ul className="alert-list">
        {alerts.map((alert) => {
          const resolved = alert.state === "resolved";
          return (
            <li className={`alert-row status-${statusFor(alert)}${alert.acknowledged && !resolved ? " is-acked" : ""}`} key={alert.id}>
              <StatusBadge status={statusFor(alert)} label={resolved ? "Resolved" : alert.severity === "critical" ? "Critical" : "Warning"} />
              <div className="alert-body">
                <div className="alert-message">{alert.message}</div>
                <div className="muted alert-meta">
                  {alert.ruleName}
                  {showAgent && (
                    <>
                      {" · "}
                      <Link href={`/agents/${alert.agentId}/dashboard`}>{alert.agentName}</Link>
                    </>
                  )}
                  {" · "}
                  {resolved ? `resolved ${since(alert.resolvedAt)}` : `since ${since(alert.firedAt)}`}
                  {resolved && alert.acknowledged && " · was acknowledged"}
                </div>
              </div>
              <div className="alert-actions">
                {!resolved &&
                  (alert.acknowledged ? (
                    <>
                      <span className="muted ack-note">✓ Acknowledged {since(alert.acknowledgedAt)}</span>
                      {onUnacknowledge && (
                        <button type="button" className="secondary" onClick={() => onUnacknowledge(alert.id)}>
                          Undo
                        </button>
                      )}
                    </>
                  ) : (
                    <button type="button" className="secondary" onClick={() => onAcknowledge?.(alert.id)}>
                      Acknowledge
                    </button>
                  ))}
              </div>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
