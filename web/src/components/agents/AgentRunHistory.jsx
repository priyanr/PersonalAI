"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { apiGet } from "@/lib/api";
import { AGENT_RUN_STATUS_LABELS, AGENT_RUN_TRIGGER_LABELS, AgentRunStatus } from "@/lib/enums";
import { formatTokens, tokenBreakdown } from "@/lib/format";

function statusClass(status) {
  if (status === AgentRunStatus.Succeeded) return "status-succeeded";
  if (status === AgentRunStatus.Failed) return "status-failed";
  return "status-running";
}

export default function AgentRunHistory({ agentId, refreshKey }) {
  const [runs, setRuns] = useState([]);
  const [status, setStatus] = useState("loading");
  const [error, setError] = useState(null);

  useEffect(() => {
    let cancelled = false;
    let timer = null;
    setStatus("loading");

    async function poll() {
      try {
        const data = await apiGet(`/api/agents/${agentId}/runs?limit=20`);
        if (cancelled) return;
        setRuns(data);
        setStatus("succeeded");
        if (data.some((run) => run.status === AgentRunStatus.Running)) {
          timer = setTimeout(poll, 2000);
        }
      } catch (err) {
        if (cancelled) return;
        setError(err.message);
        setStatus("failed");
      }
    }

    poll();
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [agentId, refreshKey]);

  return (
    <div className="skill-run-panel">
      <h2>Run history</h2>
      {status === "loading" && <p className="muted">Loading...</p>}
      {status === "failed" && <p className="error-text">{error}</p>}
      {status === "succeeded" && runs.length === 0 && <p className="muted">No runs yet.</p>}
      {runs.length > 0 && (
        <ul className="run-history-list">
          {runs.map((run) => (
            <li key={run.id}>
              <Link href={`/agents/${agentId}/runs/${run.id}`} className="run-history-item">
                <span className="run-history-item-header">
                  <span>
                    {new Date(run.startedAt).toLocaleString()} · {AGENT_RUN_TRIGGER_LABELS[run.trigger]} · {run.llmCallCount ?? 0} LLM{" "}
                    {run.llmCallCount === 1 ? "call" : "calls"} ·{" "}
                    <span title={tokenBreakdown(run.totalInputTokens ?? 0, run.totalOutputTokens ?? 0)}>
                      {formatTokens((run.totalInputTokens ?? 0) + (run.totalOutputTokens ?? 0))} tokens
                    </span>
                  </span>
                  <span className={`run-status-badge ${statusClass(run.status)}`}>
                    {AGENT_RUN_STATUS_LABELS[run.status]}
                  </span>
                </span>
                {run.summary && <span className="run-history-summary">{run.summary}</span>}
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
