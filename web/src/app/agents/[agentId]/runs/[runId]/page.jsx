"use client";

import { useEffect, useState } from "react";
import { useParams } from "next/navigation";
import Link from "next/link";
import { apiGet } from "@/lib/api";
import RunTranscript from "@/components/agents/RunTranscript";
import { AGENT_RUN_STATUS_LABELS, AGENT_RUN_TRIGGER_LABELS, AgentRunStatus } from "@/lib/enums";
import { formatTokens, tokenBreakdown } from "@/lib/format";
import { useSandboxStatus } from "@/hooks/useSandboxStatus";
import SandboxFileBrowser from "@/components/sandbox/SandboxFileBrowser";

function statusClass(status) {
  if (status === AgentRunStatus.Succeeded) return "status-succeeded";
  if (status === AgentRunStatus.Failed) return "status-failed";
  return "status-running";
}

export default function AgentRunDetailPage() {
  const { agentId, runId } = useParams();
  const [run, setRun] = useState(null);
  const [status, setStatus] = useState("loading");
  const [error, setError] = useState(null);
  const { canBrowse: canBrowseSandbox, refresh: refreshSandboxStatus } = useSandboxStatus(runId);
  const [sandboxBrowserOpen, setSandboxBrowserOpen] = useState(false);

  useEffect(() => {
    let cancelled = false;
    let timer = null;

    async function poll() {
      try {
        const data = await apiGet(`/api/agents/${agentId}/runs/${runId}`);
        if (cancelled) return;
        setRun(data);
        setStatus("succeeded");
        // A skill may create the run's sandbox at any point while it's running, so re-check alongside
        // each poll rather than only once on mount.
        refreshSandboxStatus();
        if (data.status === AgentRunStatus.Running) {
          timer = setTimeout(poll, 1500);
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
  }, [agentId, runId, refreshSandboxStatus]);

  return (
    <main>
      <div className="top-nav">
        <h1>Run detail</h1>
        <div className="top-nav-links">
          <Link href={`/agents/${agentId}`}>← Agent</Link>
        </div>
      </div>

      {status === "loading" && <p className="muted">Loading...</p>}
      {status === "failed" && <p className="error-text">{error}</p>}

      {run && (
        <>
          <p className="muted">
            {new Date(run.startedAt).toLocaleString()} · {AGENT_RUN_TRIGGER_LABELS[run.trigger]} ·{" "}
            <span className={`run-status-badge ${statusClass(run.status)}`}>
              {AGENT_RUN_STATUS_LABELS[run.status]}
            </span>
            {` · ${run.llmCallCount ?? 0} LLM ${run.llmCallCount === 1 ? "call" : "calls"} · `}
            <span title={tokenBreakdown(run.totalInputTokens ?? 0, run.totalOutputTokens ?? 0)}>
              {formatTokens((run.totalInputTokens ?? 0) + (run.totalOutputTokens ?? 0))} tokens
            </span>
            {run.completedAt && ` · completed ${new Date(run.completedAt).toLocaleString()}`}
            {canBrowseSandbox && (
              <span className="run-status-line-actions">
                {" · "}
                <button type="button" className="secondary" onClick={() => setSandboxBrowserOpen(true)}>
                  Sandbox files
                </button>
              </span>
            )}
          </p>
          {run.summary && <p>{run.summary}</p>}
          <RunTranscript entries={run.transcript} />
        </>
      )}

      {sandboxBrowserOpen && <SandboxFileBrowser ownerId={runId} onClose={() => setSandboxBrowserOpen(false)} />}
    </main>
  );
}
