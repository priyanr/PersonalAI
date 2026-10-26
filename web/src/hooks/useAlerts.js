"use client";

import { useCallback, useEffect, useState } from "react";
import { apiGet, apiPost } from "@/lib/api";

// Fired after any acknowledge/undo so every mounted list and the nav badge refetch at once, instead of each waiting
// for its own poll.
export const ALERTS_CHANGED = "alerts:changed";

function announceChange() {
  if (typeof window !== "undefined") window.dispatchEvent(new Event(ALERTS_CHANGED));
}

// Alerts for one agent (or all agents) in a given state, refetched on an interval. Acknowledging updates the
// local list straight away so the button reacts before the next poll. `enabled: false` skips fetching (a tab that
// is not showing).
export function useAlerts({ state = "active", agentId = null, refreshMs = 30000, enabled = true } = {}) {
  const [alerts, setAlerts] = useState(null);
  const [error, setError] = useState(null);

  const load = useCallback(async () => {
    const params = new URLSearchParams({ state });
    if (agentId) params.set("agentId", agentId);
    try {
      setAlerts(await apiGet(`/api/alerts?${params}`));
      setError(null);
    } catch (err) {
      setError(err.message);
    }
  }, [state, agentId]);

  useEffect(() => {
    if (!enabled) return undefined;
    load();
    window.addEventListener(ALERTS_CHANGED, load);
    const timer = refreshMs ? setInterval(load, refreshMs) : null;
    return () => {
      window.removeEventListener(ALERTS_CHANGED, load);
      if (timer) clearInterval(timer);
    };
  }, [load, refreshMs, enabled]);

  const setAcknowledged = useCallback(async (id, acknowledged) => {
    await apiPost(`/api/alerts/${id}/${acknowledged ? "acknowledge" : "unacknowledge"}`, {});
    const at = acknowledged ? new Date().toISOString() : null;
    setAlerts((prev) => prev?.map((a) => (a.id === id ? { ...a, acknowledged, acknowledgedAt: at } : a)) ?? prev);
    announceChange();
  }, []);

  const acknowledge = useCallback((id) => setAcknowledged(id, true), [setAcknowledged]);
  const unacknowledge = useCallback((id) => setAcknowledged(id, false), [setAcknowledged]);

  // Everything still firing and unacknowledged (optionally of this hook's agent).
  const acknowledgeAll = useCallback(async () => {
    const query = agentId ? `?agentId=${agentId}` : "";
    await apiPost(`/api/alerts/acknowledge-all${query}`, {});
    announceChange();
  }, [agentId]);

  return { alerts, error, reload: load, acknowledge, unacknowledge, acknowledgeAll };
}
