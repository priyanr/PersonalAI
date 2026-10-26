"use client";

import { useEffect, useState } from "react";
import "../dashboard/dashboard.css";
import { apiGet } from "@/lib/api";
import { ALERTS_CHANGED } from "@/hooks/useAlerts";

// A small count next to "Alerts" in the nav: only the alerts that still need attention (firing and not acknowledged),
// so acknowledging one takes it off the count. Critical ones are called out in text so the distinction never relies on
// colour alone. Refetches as soon as an alert is acknowledged anywhere, not only on the poll.
export default function AlertsNavBadge() {
  const [summary, setSummary] = useState(null);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const result = await apiGet("/api/alerts/summary");
        if (!cancelled) setSummary(result);
      } catch {
        // the nav must never break because alerts are unavailable
      }
    }
    load();
    window.addEventListener(ALERTS_CHANGED, load);
    const timer = setInterval(load, 30000);
    return () => {
      cancelled = true;
      window.removeEventListener(ALERTS_CHANGED, load);
      clearInterval(timer);
    };
  }, []);

  if (!summary || summary.unacknowledged === 0) return null;
  const critical = summary.unacknowledgedCritical;
  const label =
    `${summary.unacknowledged} need attention${critical > 0 ? `, ${critical} critical` : ""}` +
    (summary.acknowledged > 0 ? `; ${summary.acknowledged} acknowledged` : "");
  return (
    <span className={`nav-badge${critical > 0 ? " is-critical" : ""}`} title={label} aria-label={label}>
      {summary.unacknowledged}
    </span>
  );
}
