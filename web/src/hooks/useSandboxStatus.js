"use client";

import { useCallback, useEffect, useState } from "react";
import { apiGet } from "@/lib/api";

// Backs the "Sandbox files" button's visibility: a plain read (GET /api/sandbox/{ownerId}/status never
// creates or recreates a container as a side effect), so calling this on every page load is safe and
// never causes container churn. ownerId is a ChatSession id or an AgentRun id -- whatever id the
// SandboxOwner passed to the model's own sandbox tool used for this same chat/run.
export function useSandboxStatus(ownerId) {
  const [canBrowse, setCanBrowse] = useState(false);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    if (!ownerId) return;
    setLoading(true);
    try {
      const status = await apiGet(`/api/sandbox/${ownerId}/status`);
      setCanBrowse(Boolean(status?.canBrowse));
    } catch {
      // A transient failure here just hides the button rather than surfacing an error -- opening the
      // browser itself will report anything actually wrong.
      setCanBrowse(false);
    } finally {
      setLoading(false);
    }
  }, [ownerId]);

  useEffect(() => {
    refresh();
  }, [refresh]);

  return { canBrowse, loading, refresh };
}
